# B4 — Kiểm chứng đáp án AI & loại câu trùng: kế hoạch triển khai

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm lọc câu trùng (bỏ hẳn) và kiểm chứng đáp án tùy chọn cho câu AI, dạng thành phần thuần ở Application để worker B6 gọi: sinh → lọc trùng → kiểm chứng → lưu.

**Architecture:** Ba khối độc lập: `AiQuestionGenerator` (B3, không đổi), `AiDuplicateFilter` (hàm thuần + nguồn khóa ngân hàng ở Infrastructure), `AiAnswerVerifier` (một lần gọi model cho cả lô, lỗi → `NotChecked`). Cột mới `ai_generation_jobs.duplicates_dropped` đi qua `AiJobView` ra API.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit (`[Fact]`, `[PostgresFact]` với `PostgresFixture`), System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-11-22-b4-ai-verification-dedup-design.md`

## Global Constraints

- Sửa `docs/context.md` **trước** khi viết code (CLAUDE.md). Code/tên/comment tiếng Anh; tài liệu và câu note hiển thị tiếng Việt.
- Không commit/push khi người dùng chưa yêu cầu. Các bước "Commit" chỉ chạy khi đã được phép; nhánh làm việc `feat/ai-verification-dedup` tách từ `dev`.
- Không gọi Bedrock thật trong test; dùng `FakeAiModelClient` (tests/ExamMaker.Application.Tests/Ai/Generation).
- Không sửa `AiQuestionGenerator`/`AiPromptBuilder` (B3) ngoài việc đổi helper private của `AiOutputParser` thành `internal`.
- Giá trị `ai_verification`: đúng chuỗi `NotChecked`, `Agree`, `Disagree` (CHECK DB hiện có).
- Phiên bản prompt kiểm chứng: `answer-verify-v1`. Cấu hình: `AiGeneration:VerifyAnswers` mặc định `false`; `AiGeneration:VerifyMaxOutputTokens` mặc định `2048`; temperature kiểm chứng `0`.
- Note tiếng Việt cố định (copy chính xác):
  - Không kiểm được cả lô: `Không kiểm chứng được tự động.`
  - Model bỏ sót câu: `Mô hình không trả lời câu này.`
  - MC lệch: `Mô hình tự giải chọn {model}, đáp án gốc là {correct}.`
  - TF lệch: `Mô hình tự giải khác đáp án gốc ở ý: {labels nối bằng ", "}.`
  - ShortAnswer lệch: `Mô hình tự giải ra "{giá trị model, cắt tối đa 100 ký tự}", không khớp đáp án chấp nhận.`
  - `Agree` và câu không kiểm (Essay, cờ tắt) → note `null`.

## Review Focus

- Tiếng Việt dựng sẵn (NFC) và tổ hợp (NFD) cùng nội dung phải ra cùng khóa trùng → test ở Task 1.
- Hai câu TrueFalse cùng đề bài chung chung ("Cho các phát biểu sau") nhưng khác ý không bị coi là trùng → test ở Task 1.
- `tolerance` có số mũ cực trị (`1e500`, `1e-500`) hoặc vắng mặt không được ném lỗi khi so → test ở Task 3.
- Worker hủy (token bị cancel) phải ném `OperationCanceledException`, không bị nuốt thành `NotChecked` → test ở Task 4.
- Đầu ra kiểm chứng bọc trong một khối ```` ```json ```` vẫn được chấp nhận như B3 → test ở Task 4.

---

### Task 1: Cập nhật context.md, chuẩn hóa văn bản và `AiDuplicateFilter`

**Files:**
- Modify: `docs/context.md` (dòng phiên bản đầu file; mục 4.1.2 sau "Chi tiết triển khai B3"; mục 8.1 dòng `ai_generation_jobs`; "Lịch sử thay đổi")
- Create: `backend/src/ExamMaker.Application/Ai/AiTextNormalizer.cs`
- Create: `backend/src/ExamMaker.Application/Ai/Generation/AiDuplicateFilter.cs`
- Test: `backend/tests/ExamMaker.Application.Tests/Ai/Generation/AiDuplicateFilterTests.cs`

**Interfaces:**
- Consumes: `AiGeneratedQuestion(int Ordinal, string Stem, JsonElement Payload, string? Explanation)` (B3).
- Produces:
  - `public static class AiTextNormalizer { public static string Normalize(string text); }`
  - `public sealed record AiDuplicateFilterResult(IReadOnlyList<AiGeneratedQuestion> Kept, int Dropped);`
  - `public static class AiDuplicateFilter { public static string Key(string type, string stem, JsonElement payload); public static AiDuplicateFilterResult Filter(string type, IReadOnlyList<AiGeneratedQuestion> questions, IReadOnlySet<string> existingKeys); }`

- [ ] **Step 1: Sửa `docs/context.md` lên 0.18**

Đổi dòng đầu thành "Phiên bản 0.18". Thêm mục "#### Chi tiết triển khai B4" ngay sau khối B3 trong 4.1.2, tóm tắt từ spec: thứ tự sinh → lọc trùng → kiểm chứng; khóa trùng (stem + text phương án/ý, không đáp án, chuẩn hóa NFC/lowercase/gộp khoảng trắng); so với mọi câu cùng môn+chương; câu trùng bị bỏ, giữ ordinal gốc, đếm vào `duplicates_dropped`, tất cả trùng vẫn `Succeeded`; kiểm chứng một lần gọi, `answer-verify-v1`, cờ `AiGeneration:VerifyAnswers`, lỗi → `NotChecked` không fail job, không sửa lại, cancel/callback lỗi vẫn ném lên; so sánh theo dạng; note cố định. Mục 8.1 `ai_generation_jobs` thêm `duplicates_dropped`. Thêm "**0.18 — 22/11/2026**" vào lịch sử thay đổi.

- [ ] **Step 2: Viết test thất bại**

```csharp
public sealed class AiDuplicateFilterTests
{
    // Helpers: Mc(ordinal, stem, a, b, c, d, correct) and Tf(ordinal, stem, a, b, c, d) build AiGeneratedQuestion
    // with payloads shaped as docs/context.md 8.2.

    [Fact] public void Normalize_collapses_whitespace_lowercases_and_composes()
        => Assert.Equal("cho hàm số y = x^2", AiTextNormalizer.Normalize("  Cho\tHÀM   số\n y = x^2 "));

    [Fact] public void Nfd_and_nfc_vietnamese_produce_same_key()   // Review Focus
    {
        var nfc = "Phương trình bậc hai".Normalize(NormalizationForm.FormC);
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.Equal(AiDuplicateFilter.Key("ShortAnswer", nfc, ShortPayload()), AiDuplicateFilter.Key("ShortAnswer", nfd, ShortPayload()));
    }

    [Fact] public void Mc_key_ignores_correct_label_but_includes_option_texts()
    {
        Assert.Equal(Key(Mc(1, "Câu", "1", "2", "3", "4", "A")), Key(Mc(2, "Câu", "1", "2", "3", "4", "C")));
        Assert.NotEqual(Key(Mc(1, "Câu", "1", "2", "3", "4", "A")), Key(Mc(2, "Câu", "1", "2", "3", "5", "A")));
    }

    [Fact] public void Tf_with_generic_stem_and_different_statements_is_not_duplicate()   // Review Focus
    {
        var result = AiDuplicateFilter.Filter("TrueFalse",
            [Tf(1, "Cho các phát biểu sau", "p", "q", "r", "s"), Tf(2, "Cho các phát biểu sau", "p", "q", "r", "t")],
            new HashSet<string>());
        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(0, result.Dropped);
    }

    [Fact] public void In_batch_duplicate_keeps_lowest_ordinal()
    {
        var result = AiDuplicateFilter.Filter("MultipleChoice",
            [Mc(1, "Câu A", "1", "2", "3", "4", "A"), Mc(2, " câu a ", "1", "2", "3", "4", "B"), Mc(3, "Câu B", "1", "2", "3", "4", "A")],
            new HashSet<string>());
        Assert.Equal([1, 3], result.Kept.Select(q => q.Ordinal));
        Assert.Equal(1, result.Dropped);
    }

    [Fact] public void Bank_duplicate_is_dropped_and_ordinals_keep_gaps()
    {
        var bank = new HashSet<string> { Key(Mc(9, "Câu B", "1", "2", "3", "4", "D")) };
        var result = AiDuplicateFilter.Filter("MultipleChoice",
            [Mc(1, "Câu A", "1", "2", "3", "4", "A"), Mc(2, "Câu B", "1", "2", "3", "4", "A"), Mc(3, "Câu C", "1", "2", "3", "4", "A")], bank);
        Assert.Equal([1, 3], result.Kept.Select(q => q.Ordinal));
        Assert.Equal(1, result.Dropped);
    }

    [Fact] public void All_duplicates_returns_empty_kept()
    {
        var q = Mc(1, "Câu A", "1", "2", "3", "4", "A");
        var result = AiDuplicateFilter.Filter("MultipleChoice", [q], new HashSet<string> { Key(q) });
        Assert.Empty(result.Kept);
        Assert.Equal(1, result.Dropped);
    }

    [Fact] public void ShortAnswer_and_essay_keys_use_stem_only()
        => Assert.Equal(AiDuplicateFilter.Key("ShortAnswer", "Tính x", ShortPayload()), AiDuplicateFilter.Key("Essay", "tính  X", EssayPayload()));
}
```

- [ ] **Step 3: Chạy test, xác nhận thất bại**

Run: `dotnet test backend/tests/ExamMaker.Application.Tests --filter FullyQualifiedName~AiDuplicateFilterTests`
Expected: lỗi biên dịch (`AiTextNormalizer`, `AiDuplicateFilter` chưa tồn tại).

- [ ] **Step 4: Cài đặt**

- `AiTextNormalizer.Normalize`: `Normalize(NormalizationForm.FormC)` → `ToLowerInvariant()` → thay mọi chuỗi `\s+` bằng một dấu cách → `Trim()`. Giữ dấu câu, LaTeX.
- `AiDuplicateFilter.Key`: nối bằng ký tự `\u001F` các phần đã chuẩn hóa: stem; với `MultipleChoice` thêm `options[*].text` theo thứ tự mảng; với `TrueFalse` thêm `statements[*].text`; `ShortAnswer`/`Essay` chỉ stem. Payload đến đây đã qua `QuestionRules`, nên đọc thẳng thuộc tính.
- `Filter`: duyệt theo thứ tự đầu vào, giữ `HashSet` khóa đã thấy (khởi đầu là bản sao `existingKeys`), câu có khóa đã thấy → bỏ và tăng `Dropped`.

- [ ] **Step 5: Chạy test, xác nhận qua**

Run: lệnh ở Step 3. Expected: PASS toàn bộ.

- [ ] **Step 6: Commit (khi được phép)**

```bash
git add docs/context.md backend/src/ExamMaker.Application/Ai backend/tests/ExamMaker.Application.Tests/Ai/Generation/AiDuplicateFilterTests.cs
git commit -m "feat(ai): add normalized duplicate filter for generated questions"
```

---

### Task 2: Cột `duplicates_dropped`, nguồn khóa ngân hàng và API

**Files:**
- Modify: `backend/src/ExamMaker.Domain/Ai/AiGenerationJob.cs` (thêm `public int DuplicatesDropped { get; set; }`)
- Modify: `backend/src/ExamMaker.Infrastructure/Persistence/Configurations/AiGenerationJobConfiguration.cs` (cột `duplicates_dropped`, default 0, CHECK `ck_ai_generation_jobs_duplicates_dropped`: `duplicates_dropped >= 0`)
- Create: migration `AddAiJobDuplicatesDropped` trong `backend/src/ExamMaker.Infrastructure/Persistence/Migrations/`
- Create: `backend/src/ExamMaker.Application/Ai/IAiDuplicateKeySource.cs`
- Create: `backend/src/ExamMaker.Infrastructure/Ai/EfAiDuplicateKeySource.cs`
- Modify: `backend/src/ExamMaker.Infrastructure/Ai/AiServiceCollectionExtensions.cs` (đăng ký scoped)
- Modify: `backend/src/ExamMaker.Application/Ai/AiJobView.cs`, `backend/src/ExamMaker.Infrastructure/Ai/EfAiJobReader.cs`
- Modify: `docs/api/openapi.yaml` (schema `AiJob`)
- Modify (constructor `AiJobView` mới): `backend/tests/ExamMaker.Api.Tests/AiEndpointsTests.cs:344`, `backend/tests/ExamMaker.Application.Tests/Ai/AiJobQueriesTests.cs:31`
- Test: `backend/tests/ExamMaker.Infrastructure.Tests/Ai/EfAiDuplicateKeySourceTests.cs`, bổ sung `EfAiJobReaderTests.cs`

**Interfaces:**
- Consumes: `AiDuplicateFilter.Key(string type, string stem, JsonElement payload)` (Task 1).
- Produces:
  - `public interface IAiDuplicateKeySource { Task<IReadOnlySet<string>> GetKeysAsync(Guid subjectId, Guid topicId, CancellationToken ct); }`
  - `AiGenerationJob.DuplicatesDropped` (int) — worker B6 gán khi lưu job `Succeeded`.
  - `AiJobView` thêm tham số `int DuplicatesDropped` ngay sau `long OutputTokens`; JSON `duplicatesDropped`.

- [ ] **Step 1: Viết test thất bại**

`EfAiDuplicateKeySourceTests` (`[PostgresFact]`, `IClassFixture<PostgresFixture>`; tạo câu theo mẫu `new Question { ... }` ở `ExamManagementTests.cs:38`, dùng môn/chương seed và user từ `AiTestData.CreateUserAsync`):
- `Returns_keys_for_all_statuses_and_authors_in_topic`: chèn 1 câu `Draft` của user A và 1 câu `Archived` của user B cùng môn+chương → kết quả chứa đúng `AiDuplicateFilter.Key(type, stem, payload)` của cả hai.
- `Excludes_other_topics`: câu ở chương khác cùng môn → khóa không có trong kết quả.

`EfAiJobReaderTests` bổ sung:
- `Duplicates_dropped_round_trips`: lưu job `DuplicatesDropped = 2` → `GetAsync` trả `DuplicatesDropped == 2`.
- `Negative_duplicates_dropped_is_rejected`: lưu job `DuplicatesDropped = -1` → `SaveChangesAsync` ném `DbUpdateException` (CHECK).

- [ ] **Step 2: Chạy test, xác nhận thất bại**

Run: `dotnet test backend/tests/ExamMaker.Infrastructure.Tests --filter "FullyQualifiedName~EfAiDuplicateKeySource|FullyQualifiedName~EfAiJobReader"`
Expected: lỗi biên dịch (`DuplicatesDropped`, `EfAiDuplicateKeySource` chưa có).

- [ ] **Step 3: Cài đặt entity, cấu hình, migration**

Thêm property + cấu hình, rồi tạo migration:
`cd backend && dotnet ef migrations add AddAiJobDuplicatesDropped --project src/ExamMaker.Infrastructure --output-dir Persistence/Migrations`
Kiểm tra migration chỉ `AddColumn` (`integer`, `nullable: false`, `defaultValue: 0`) + `AddCheckConstraint`; `Down` gỡ cả hai.

- [ ] **Step 4: Cài đặt `EfAiDuplicateKeySource` và reader/view**

- `EfAiDuplicateKeySource(ExamMakerDbContext db)`: `AsNoTracking`, lọc `SubjectId == subjectId && TopicId == topicId`, chỉ select `Type, Stem, Payload`; tính khóa ở C# bằng `AiDuplicateFilter.Key(type, stem, JsonDocument.Parse(payload).RootElement)` (dispose document), trả `HashSet<string>` ordinal.
- Đăng ký `services.AddScoped<IAiDuplicateKeySource, EfAiDuplicateKeySource>();`.
- `EfAiJobReader`: thêm `DuplicatesDropped` vào `JobRow`, projection và `AiJobView`. Sửa hai test dựng `AiJobView` (truyền `0`).
- OpenAPI `AiJob`: thêm `duplicatesDropped` vào `required` và `properties`: `duplicatesDropped: { type: integer, minimum: 0, description: Số câu AI sinh ra bị bỏ vì trùng (trong lô hoặc với ngân hàng cùng môn + chương). }`.

- [ ] **Step 5: Chạy test, xác nhận qua**

Run: `dotnet test backend/tests/ExamMaker.Infrastructure.Tests --filter "FullyQualifiedName~Ai"` rồi `dotnet test backend/tests/ExamMaker.Api.Tests --filter FullyQualifiedName~AiEndpoints`
Expected: PASS (test Postgres cần biến môi trường DB như CI; nếu bị skip local thì ghi rõ khi báo cáo).

- [ ] **Step 6: Commit (khi được phép)**

```bash
git add backend/src backend/tests docs/api/openapi.yaml
git commit -m "feat(ai): track dropped duplicates on jobs and read bank duplicate keys"
```

---

### Task 3: So sánh đáp án theo dạng (`AiAnswerComparer`)

**Files:**
- Create: `backend/src/ExamMaker.Application/Ai/Verification/AiVerificationContracts.cs`
- Create: `backend/src/ExamMaker.Application/Ai/Verification/AiAnswerComparer.cs`
- Test: `backend/tests/ExamMaker.Application.Tests/Ai/Verification/AiAnswerComparerTests.cs`

**Interfaces:**
- Consumes: `AiTextNormalizer.Normalize` (Task 1).
- Produces:
  - `public static class AiVerificationValues { public const string NotChecked = "NotChecked", Agree = "Agree", Disagree = "Disagree"; }`
  - `public sealed record AiVerificationOutcome(string Verification, string? Note);`
  - `public static class AiVerificationNotes` với `Unavailable`, `Unanswered` (const) và `MultipleChoice(string model, string correct)`, `TrueFalse(IEnumerable<string> labels)`, `ShortAnswer(string model)` — đúng câu ở Global Constraints.
  - `public static class AiAnswerComparer { public static AiVerificationOutcome Compare(string type, JsonElement payload, JsonElement modelAnswer); }` — `modelAnswer` đã được parser (Task 4) kiểm hình dạng: MC là chuỗi `A`–`D`, TF là object `a`–`d` boolean, ShortAnswer là chuỗi.

- [ ] **Step 1: Viết test thất bại**

```csharp
[Fact] public void Mc_same_label_agrees() => Assert.Equal(new AiVerificationOutcome("Agree", null), Compare("MultipleChoice", Mc("B"), "\"B\""));
[Fact] public void Mc_other_label_disagrees_with_note()
    => Assert.Equal(new AiVerificationOutcome("Disagree", "Mô hình tự giải chọn C, đáp án gốc là B."), Compare("MultipleChoice", Mc("B"), "\"C\""));
[Fact] public void Tf_all_match_agrees() => Assert.Equal("Agree", Compare("TrueFalse", Tf(true, false, true, false), """{"a":true,"b":false,"c":true,"d":false}""").Verification);
[Fact] public void Tf_mismatch_lists_labels()
    => Assert.Equal("Mô hình tự giải khác đáp án gốc ở ý: b, d.", Compare("TrueFalse", Tf(true, false, true, false), """{"a":true,"b":true,"c":true,"d":true}""").Note);
[Theory]
[InlineData("12,5", """["12,5"]""", null, "Agree")]
[InlineData("12.5", """["12,5"]""", null, "Agree")]
[InlineData("12.51", """["12,5"]""", "0.01", "Agree")]
[InlineData("12.52", """["12,5"]""", "0.01", "Disagree")]
[InlineData("13", """["12","13"]""", null, "Agree")]
[InlineData("12 m/s", """["12"]""", null, "Disagree")]
[InlineData("99", """["12"]""", "1e500", "Agree")]      // Review Focus: huge tolerance must not throw
[InlineData("12", """["12"]""", "1e-500", "Agree")]     // Review Focus: tiny tolerance must not throw
public void Number_answers(string model, string accepted, string? tolerance, string expected) { /* build number payload, assert Verification */ }
[Fact] public void Text_answer_matches_after_normalization() => Assert.Equal("Agree", Compare("ShortAnswer", Text("Quang hợp"), "\"  quang   HỢP \"").Verification);
[Fact] public void Short_answer_disagree_note_truncates_model_value_to_100_chars()
    => Assert.Equal($"Mô hình tự giải ra \"{new string('x', 100)}\", không khớp đáp án chấp nhận.", Compare("ShortAnswer", Text("y"), $"\"{new string('x', 150)}\"").Note);
```

- [ ] **Step 2: Chạy test, xác nhận thất bại**

Run: `dotnet test backend/tests/ExamMaker.Application.Tests --filter FullyQualifiedName~AiAnswerComparerTests`
Expected: lỗi biên dịch.

- [ ] **Step 3: Cài đặt `AiAnswerComparer.Compare`**

Số: thay `,` → `.`, `decimal.TryParse(..., NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)`; model không parse được → `Disagree`. Tolerance: không có/null → 0; `JsonElement.TryGetDecimal` thất bại với số lớn → coi như mọi số đều khớp, với số rất nhỏ → 0 (phân biệt bằng dấu số mũ trong `GetRawText()`). Khớp nếu `|model − accepted| ≤ tolerance` với bất kỳ đáp án nào. Text: so `AiTextNormalizer.Normalize` bằng nhau với bất kỳ đáp án. Type khác ba dạng trên → `ArgumentOutOfRangeException`.

- [ ] **Step 4: Chạy test, xác nhận qua** — lệnh ở Step 2, Expected: PASS.

- [ ] **Step 5: Commit (khi được phép)**

```bash
git add backend/src/ExamMaker.Application/Ai/Verification backend/tests/ExamMaker.Application.Tests/Ai/Verification
git commit -m "feat(ai): compare model-solved answers per question type"
```

---

### Task 4: Prompt, parser và `AiAnswerVerifier`

**Files:**
- Modify: `backend/src/ExamMaker.Application/Ai/Generation/AiOutputParser.cs` (đổi `StripFence`, `ParseJson`, `HasExactKeys` từ `private` sang `internal`, không đổi hành vi)
- Modify: `backend/src/ExamMaker.Application/Ai/Generation/AiQuestionGenerator.cs` (chỉ `AiGenerationOptions`: thêm `public bool VerifyAnswers { get; set; }` và `public int VerifyMaxOutputTokens { get; set; } = 2048;`)
- Create: `backend/src/ExamMaker.Application/Ai/Verification/AiVerificationPromptBuilder.cs`
- Create: `backend/src/ExamMaker.Application/Ai/Verification/AiVerificationParser.cs`
- Create: `backend/src/ExamMaker.Application/Ai/Verification/AiAnswerVerifier.cs`
- Modify: `docs/tasks.md` (tick B4 + dòng "Hoàn thành ...")
- Test: `backend/tests/ExamMaker.Application.Tests/Ai/Verification/AiAnswerVerifierTests.cs`

**Interfaces:**
- Consumes: `IAiModelClient`, `AiModelRequest`, `AiModelResponse`, `AiStopReason`, `AiModelException`, `AiOutputException` (B3); `AiAnswerComparer.Compare`, `AiVerificationOutcome`, `AiVerificationValues`, `AiVerificationNotes` (Task 3); `AiGeneratedQuestion` (B3).
- Produces:
  - `public static class AiVerificationPromptBuilder { public const string Version = "answer-verify-v1"; public static AiPrompt Build(string type, IReadOnlyList<AiGeneratedQuestion> questions); }`
  - `public static class AiVerificationParser { public static IReadOnlyDictionary<int, JsonElement> Parse(string text, string type, IReadOnlyCollection<int> ordinals); }` — ném `AiOutputException` khi JSON/schema sai.
  - `public sealed class AiAnswerVerifier(IAiModelClient client, AiGenerationOptions options) { public Task<IReadOnlyDictionary<int, AiVerificationOutcome>> VerifyAsync(string type, IReadOnlyList<AiGeneratedQuestion> questions, Func<AiModelResponse, CancellationToken, Task>? onModelCall, CancellationToken cancellationToken); }` — trả kết quả cho **mọi** ordinal đầu vào.

- [ ] **Step 1: Viết test thất bại** (dùng `FakeAiModelClient`; helper `Questions(...)` dựng `AiGeneratedQuestion`)

- `Disabled_returns_not_checked_without_calling_model`: `VerifyAnswers=false` → mọi outcome `("NotChecked", null)`, `_model.Requests` rỗng.
- `Essay_only_batch_does_not_call_model`: bật, type `Essay` → `NotChecked`/null, không request.
- `One_call_for_whole_batch_with_verify_settings`: 3 câu MC → đúng 1 request, `Temperature == 0`, `MaxOutputTokens == options.VerifyMaxOutputTokens`, `request.System == AiVerificationPromptBuilder.Build("MultipleChoice", questions).System`.
- `Prompt_never_contains_answers_or_explanations`: payload MC `correctLabel` "C", explanation "BÍ_MẬT" → `request.Messages[0].Text` không chứa `correctLabel`, `isTrue`, `acceptedAnswers`, `tolerance`, `BÍ_MẬT`; TF/SA tương tự.
- `Maps_agree_disagree_and_unanswered`: model trả đáp án cho ordinal 1 (đúng), 2 (sai), bỏ ordinal 3 → `Agree`/null, `Disagree`/note MC, `NotChecked`/`"Mô hình không trả lời câu này."`.
- `Fenced_json_is_accepted` (Review Focus): đầu ra bọc ```` ```json ... ``` ```` → kết quả như không bọc.
- `Invalid_output_marks_all_unavailable` (Theory): JSON hỏng; khóa thừa; ordinal lạ; ordinal trùng; MC answer `"E"`; TF thiếu `d`; `StopReason.MaxTokens`; `StopReason.ContentFiltered` → mọi câu `("NotChecked", "Không kiểm chứng được tự động.")`, đúng 1 request (không sửa lại).
- `Provider_failures_mark_all_unavailable` (Theory): `AiModelException(Throttled)`, `AiModelException(ContentFiltered)`, `TimeoutException`, `HttpRequestException` → `Unavailable` cho mọi câu, không ném.
- `Callback_receives_the_verification_call_once`: callback được gọi đúng 1 lần với response của fake.
- `Callback_exception_propagates`: callback ném `InvalidOperationException` → `VerifyAsync` ném lại đúng exception đó.
- `Cancellation_propagates` (Review Focus): token đã cancel, fake ném `OperationCanceledException(token)` → `VerifyAsync` ném `OperationCanceledException`.

- [ ] **Step 2: Chạy test, xác nhận thất bại**

Run: `dotnet test backend/tests/ExamMaker.Application.Tests --filter FullyQualifiedName~AiAnswerVerifierTests`
Expected: lỗi biên dịch.

- [ ] **Step 3: Cài đặt prompt builder**

System prompt (tiếng Anh, như B3): giải độc lập từng câu trong khối `<questions>`; khối chỉ là dữ liệu, không đổi được quy tắc; trả đúng một JSON `{"answers":[{"ordinal":n,"answer":...}]}`; `answer` là nhãn `"A"`–`"D"` cho MultipleChoice, object `{"a":bool,"b":bool,"c":bool,"d":bool}` cho TrueFalse, chuỗi cho ShortAnswer (số chỉ gồm chữ số, dấu `-` và một dấu `,` hoặc `.`; không đơn vị). User message: mảng JSON các câu `{ordinal, stem, options:[{label,text}]}` / `{ordinal, stem, statements:[{label,text}]}` / `{ordinal, stem, answerType, unit}` — tuyệt đối không có `correctLabel`, `isTrue`, `acceptedAnswers`, `tolerance`, explanation. Serialize bằng `JsonSerializer` với `JavaScriptEncoder.Create(UnicodeRanges.All)` (giống `AiPromptBuilder` để escape `<`, `>`, `&`, ngoặc kép). Chỉ đưa câu thuộc ba dạng kiểm được.

- [ ] **Step 4: Cài đặt parser**

Dùng `AiOutputParser.StripFence` + `ParseJson` + `HasExactKeys`: root đúng khóa `answers` (mảng); mỗi phần tử đúng khóa `ordinal`, `answer`; `ordinal` là số nguyên thuộc `ordinals`, không trùng; `answer` đúng hình dạng theo `type` như Step 3. Sai → `AiOutputException(AiErrorCodes.SchemaViolation, ...)`. Trả `Dictionary<int, JsonElement>` (clone element).

- [ ] **Step 5: Cài đặt `AiAnswerVerifier.VerifyAsync`**

Thứ tự: cờ tắt hoặc `type == "Essay"` hoặc danh sách rỗng → mọi câu `NotChecked`/null. Gọi `client.InvokeAsync(new AiModelRequest(prompt.System, [user], options.VerifyMaxOutputTokens, 0), ct)` trong `try`; bắt `AiModelException`, `TimeoutException`, `HttpRequestException`, `IOException`, và `OperationCanceledException` **chỉ khi** `!cancellationToken.IsCancellationRequested` → mọi câu `Unavailable`. Gọi `onModelCall` **ngoài** `try` (exception đi lên). `StopReason` là `MaxTokens`/`ContentFiltered` → `Unavailable`. Parse trong `try` bắt `AiOutputException` → `Unavailable`. Còn lại: ordinal có đáp án → `AiAnswerComparer.Compare(type, q.Payload, answer)`; thiếu → `("NotChecked", AiVerificationNotes.Unanswered)`.

- [ ] **Step 6: Chạy test, xác nhận qua**

Run: lệnh ở Step 2. Expected: PASS.

- [ ] **Step 7: Chạy toàn bộ solution**

Run: `cd backend && dotnet build && dotnet test`
Expected: build không warning mới, mọi test PASS (test Postgres chạy khi có DB như CI; nếu skip local thì nêu rõ).

- [ ] **Step 8: Cập nhật `docs/tasks.md`**

Tick `- [x] B4.` và thêm dòng "Hoàn thành 22/11/2026: ..." tóm tắt các lớp (`AiDuplicateFilter`, `IAiDuplicateKeySource`, `AiAnswerVerifier`, cột `duplicates_dropped`), ghi rõ việc gắn vào worker thuộc B6 và số test.

- [ ] **Step 9: Commit (khi được phép)**

```bash
git add backend docs/tasks.md
git commit -m "feat(ai): add optional answer verification for generated questions"
```
