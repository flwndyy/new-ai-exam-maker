# B10 Exam Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two snapshot-only export endpoints (`student-sheet`, `answer-key`) plus a printable preview page, with tests proving answers never reach the student sheet.

**Architecture:** Pure export logic (permission/state gate, whitelist payload projection, DTO builders) lives in `ExamMaker.Application/Exams/ExamExport.cs` and is unit-tested first. `ExamManagement` (Infrastructure) only loads the version with its snapshot rows and calls the builders; `ExamVersionsController` exposes two GET routes. Frontend adds `/exams/print/?id=&vid=&mode=` using dedicated student/answer item components and links from `/exams/view/`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core + Npgsql, System.Text.Json (`JsonElement`/`Utf8JsonWriter`), xUnit; Next.js static export, TanStack Query, `openapi-fetch` `api`, Tailwind v4 (`print:` variant), Vitest + Testing Library, KaTeX via `MathMarkdown`.

**Spec:** `docs/superpowers/specs/2026-11-24-b10-exam-export-design.md`

## Global Constraints

- Branch `feat/export-exam` (already at `dev` head `5eba0a0`). PR into `dev` (Squash).
- **Never run `git commit`/`git push` or open a PR** (user rule). Skip all commit actions; leave changes in the working tree. Task 6 writes a PR description file instead.
- `docs/context.md` and `docs/api/openapi.yaml` change first (Task 1), before any code.
- No DB schema change, no migration, no new NuGet/npm package.
- Exports read **only** `exam_version_questions` snapshot columns; never include/read `SourceQuestion`.
- Exportable statuses: exactly `Submitted`, `Approved`, `Published`. Everything else → 409 `ExportUnavailable`.
- Check order: not found or `!ExamRules.CanRead(actor, exam, status)` → 404 `NotFound`; answer key and `!ExamRules.CanReadAnswers(actor, exam)` → 403 `Forbidden`; status not exportable → 409 `ExportUnavailable`; any snapshot column null → 409 `ExportUnavailable`.
- Student payload whitelist: MC `{options:[{label,text}]}`; TF `{statements:[{label,text}]}`; ShortAnswer `{answerType, unit}` (unit null when absent); Essay `{}`. Unknown type or malformed payload → `InvalidOperationException` with a fixed message (no content) → 500 via existing global handler. Never return the raw payload.
- Forbidden keys anywhere in a student sheet: `correctLabel`, `isTrue`, `acceptedAnswers`, `tolerance`, `modelAnswer`, `rubric`, `explanation`, `difficulty`, `sourceQuestionId`, `createdBy`.
- No audit rows for exports.
- Code/comments English (XML `<summary>` on public members like existing files); UI copy in `frontend/src/messages/vi.ts`; docs Vietnamese.
- Frontend renders question content only through `MathMarkdown` (directly or via `QuestionPayloadView`); title/subject/department as plain text.
- `NEXT_PUBLIC_SCHOOL_NAME` optional, read as a literal `process.env.NEXT_PUBLIC_SCHOOL_NAME`; empty/unset → omit the school line. Never commit a real value.
- Test data synthetic only (`@example.invalid`).

## Review Focus

1. A source question edited (revision+1) after the version was published: both exports must still return the snapshot text → Task 3 PostgreSQL test.
2. Teacher in the same department opening a colleague's `Approved` (not Published) version → 404 on both; colleague's `Published` → student 200, answer 403 → Task 2 matrix test.
3. Snapshot payload carrying keys the whitelist doesn't know (future fields, e.g. `"hint"`) must not appear in the student payload → Task 2 unit test.
4. ShortAnswer snapshot without a `unit` key, and Essay with rubric → `unit: null` / `{}` → Task 2 unit test.
5. Print page opened with missing `vid` or `mode=foo` → message, zero API calls → Task 4 page test.

---

### Task 1: Docs and contract first

**Files:**
- Modify: `docs/context.md` (header version line; §3.2 row "Xuất đề học sinh"; §4.4; §9.2 new "Chi tiết triển khai B10"; §14 "Quyết định đã chốt"; "Lịch sử thay đổi" new **0.24 — 24/11/2026**)
- Modify: `docs/api/openapi.yaml:969-1011` (export endpoint descriptions), `:1560-1587` (`ErrorCode`)
- Modify: `frontend/src/lib/api/schema.d.ts` (regenerated)

**Interfaces:**
- Produces: error code `ExportUnavailable` (409) in the contract; regenerated TS types used by Tasks 4–5.

- [ ] **Step 1: Edit `docs/context.md`**
  - §3.2 row "Xuất đề học sinh": Head cell `đề trong tổ & đề của mình`; append to the row's first cell: `(chỉ phiên bản Submitted/Approved/Published)`. Same note on row "Xuất file đáp án/lời giải".
  - §4.4: add a bullet: chỉ xuất `Submitted`/`Approved`/`Published`; `Draft`/`Rejected` chưa có snapshot, `Archived` có thể đến từ `Rejected` với snapshot thiếu/nội dung chưa duyệt nên không xuất (409 `ExportUnavailable`); xem lại bản Archived dùng màn chi tiết B9. Đề học sinh chỉ chép trường được phép (whitelist) theo dạng câu.
  - §9.2 "Chi tiết triển khai B10" (~6 bullets): hai endpoint + thứ tự kiểm 404→403→409; whitelist; route `/exams/print/?id=&vid=&mode=student|answer`, mỗi chế độ chỉ gọi endpoint của nó; `window.print()`; `NEXT_PUBLIC_SCHOOL_NAME`; link từ `/exams/view/`; không ghi audit.
  - §14: decision "Head xuất đề học sinh mọi đề trong tổ (ai xem được đáp án thì xem được đề học sinh); chỉ xuất Submitted/Approved/Published. _Chốt 24/11/2026._"
- [ ] **Step 2: Edit `docs/api/openapi.yaml`**
  - `student-sheet` description: quyền = Admin tất cả; tác giả đề của mình; Head mọi đề cùng tổ; Teacher bản `Published` cùng tổ. Không thấy phiên bản → 404. Chỉ `Submitted`/`Approved`/`Published`; khác → 409 `ExportUnavailable`.
  - `answer-key` description: thêm 404/409 giống trên; quyền chặt hơn giữ nguyên.
  - `ErrorCode`: add `ExportUnavailable` to enum and the bullet `- \`ExportUnavailable\` (409): phiên bản chưa/không thể xuất (chỉ Submitted/Approved/Published)`.
- [ ] **Step 3: Regenerate and check types**

Run (from `frontend/`): `npm run api:generate` then `npm run api:check`
Expected: `api:check` exits 0; `git diff --stat src/lib/api/schema.d.ts` shows only description/enum changes.

---

### Task 2: Pure export logic (Application)

**Files:**
- Create: `backend/src/ExamMaker.Application/Exams/ExamExport.cs`
- Test: `backend/tests/ExamMaker.Api.Tests/ExamExportTests.cs` (same project as `ExamRulesTests`)

**Interfaces:**
- Consumes: `ExamRules.CanRead(AuthenticatedUser, Exam, string)`, `ExamRules.CanReadAnswers(AuthenticatedUser, Exam)`, `ExamRules.Missing()`, `ExamRules.Forbidden()`, `ExamException`.
- Produces (namespace `ExamMaker.Application.Exams`):
  - `public sealed record SheetHeader(string ExamTitle, string SubjectName, string? DepartmentName, int Grade, int VersionNo, int DurationMinutes, decimal TotalPoints);`
  - `public sealed record StudentSheetItem(int Number, decimal Points, string Type, string Stem, JsonElement Payload);`
  - `public sealed record StudentSheet(SheetHeader Header, IReadOnlyList<StudentSheetItem> Items);`
  - `public sealed record AnswerKeyItem(int Number, decimal Points, string Type, string Difficulty, string Stem, JsonElement Payload, string? Explanation);`
  - `public sealed record AnswerKey(SheetHeader Header, IReadOnlyList<AnswerKeyItem> Items);`
  - `public sealed record ExportSnapshotItem(int SortOrder, decimal Points, string? Type, string? Difficulty, string? Stem, string? PayloadJson, string? Explanation);`
  - `public static class ExamExport` with:
    - `static readonly string[] ExportableStates = ["Submitted", "Approved", "Published"];`
    - `static void Authorize(AuthenticatedUser actor, Exam exam, string status, bool answerKey)` — throws per Global Constraints order.
    - `static ExamException Unavailable()` → `new(409, "ExportUnavailable", "Chỉ xuất được phiên bản đã gửi duyệt, đã duyệt hoặc đã công bố.")`
    - `static JsonElement StudentPayload(string type, JsonElement snapshot)` — whitelist; throws `InvalidOperationException("Malformed exam snapshot payload.")`.
    - `static StudentSheet BuildStudentSheet(SheetHeader header, IEnumerable<ExportSnapshotItem> items)` and `static AnswerKey BuildAnswerKey(SheetHeader header, IEnumerable<ExportSnapshotItem> items)` — order by `SortOrder`, `Number = SortOrder`; any null `Type`/`Difficulty`/`Stem`/`PayloadJson` → `Unavailable()`. Answer key payload = parsed snapshot unchanged (`JsonElement.Clone()`).

- [ ] **Step 1: Write failing tests** (reuse the `Actor`/`Exam` helper style from `ExamRulesTests`)

```csharp
// Matrix: role × ownership × department × status × endpoint → expected code (null = allowed)
[Theory, MemberData(nameof(AccessCases))]
public void Authorize_follows_visibility_then_answer_access_then_state(string role, bool own, bool same, string status, bool answerKey, string? code)
// Cases that must appear in AccessCases (others generated from CanRead/CanReadAnswers + ExportableStates):
// ("teacher", false, true, "Approved",  false, "NotFound")
// ("teacher", false, true, "Published", false, null)
// ("teacher", false, true, "Published", true,  "Forbidden")
// ("head",    false, true, "Submitted", false, null)
// ("head",    false, true, "Submitted", true,  null)
// ("head",    false, false,"Published", false, "NotFound")
// ("teacher", true,  true, "Draft",     false, "ExportUnavailable")
// ("teacher", true,  true, "Rejected",  true,  "ExportUnavailable")
// ("admin",   false, false,"Archived",  false, "ExportUnavailable")
// ("admin",   false, false,"Published", true,  null)

[Theory]
[InlineData("MultipleChoice", """{"options":[{"label":"A","text":"x","hint":"h"},{"label":"B","text":"y"}],"correctLabel":"B","hint":"h"}""", """{"options":[{"label":"A","text":"x"},{"label":"B","text":"y"}]}""")]
[InlineData("TrueFalse", """{"statements":[{"label":"a","text":"s","isTrue":true}]}""", """{"statements":[{"label":"a","text":"s"}]}""")]
[InlineData("ShortAnswer", """{"answerType":"number","acceptedAnswers":["12"],"tolerance":0.1,"unit":"m/s"}""", """{"answerType":"number","unit":"m/s"}""")]
[InlineData("ShortAnswer", """{"answerType":"text","acceptedAnswers":["x"]}""", """{"answerType":"text","unit":null}""")]
[InlineData("Essay", """{"modelAnswer":"M","rubric":[{"criterion":"c","points":1}]}""", "{}")]
public void Student_payload_copies_only_whitelisted_fields(string type, string snapshot, string expected)
// Assert: JsonSerializer.Serialize(ExamExport.StudentPayload(type, Parse(snapshot))) == expected (compact)

[Theory]
[InlineData("Unknown", "{}")]
[InlineData("MultipleChoice", """{"options":"bad"}""")]
[InlineData("MultipleChoice", """{"options":[{"label":"A"}]}""")]
[InlineData("ShortAnswer", """{"unit":"m"}""")]
public void Student_payload_rejects_unknown_types_and_malformed_snapshots(string type, string snapshot)
// Assert.Throws<InvalidOperationException>; message equals "Malformed exam snapshot payload."

[Fact] public void Builders_number_by_sort_order_and_keep_answers_only_in_answer_key()
// items given out of order (SortOrder 2,1); student Items[0].Number == 1; student JSON (camelCase, like the API)
// contains none of the forbidden keys and not "EXPLANATION-SENTINEL"; answer key Items[*].Explanation == "EXPLANATION-SENTINEL", payload has "correctLabel".

[Fact] public void Builders_refuse_incomplete_snapshots()
// one item with PayloadJson null → ExamException Code "ExportUnavailable" from both builders.
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test backend/tests/ExamMaker.Api.Tests --filter FullyQualifiedName~ExamExportTests`
Expected: build error (`ExamExport` not defined).

- [ ] **Step 3: Implement `ExamExport.cs`** per the Interfaces block. Build `StudentPayload` with `Utf8JsonWriter` into a `MemoryStream`, then `JsonDocument.Parse(...).RootElement.Clone()`. Require `label`/`text`/`answerType` to be JSON strings; `unit` must be string or null/absent.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test backend/tests/ExamMaker.Api.Tests --filter FullyQualifiedName~ExamExportTests`
Expected: all pass.

---

### Task 3: Service, API routes, HTTP and PostgreSQL tests

**Files:**
- Modify: `backend/src/ExamMaker.Application/Exams/ExamContracts.cs` (`IExamManagement`)
- Modify: `backend/src/ExamMaker.Infrastructure/Exams/ExamManagement.cs`
- Modify: `backend/src/ExamMaker.Api/Controllers/ExamVersionsController.cs`
- Test: `backend/tests/ExamMaker.Api.Tests/ExamsHttpTests.cs` (+ `FakeExams`), `backend/tests/ExamMaker.Infrastructure.Tests/ExamManagementTests.cs`

**Interfaces:**
- Consumes: everything Task 2 produces.
- Produces:
  - `Task<StudentSheet> StudentSheetAsync(Guid actor, Guid id, CancellationToken cancellationToken);`
  - `Task<AnswerKey> AnswerKeyAsync(Guid actor, Guid id, CancellationToken cancellationToken);`
  - Routes `GET api/v1/exam-versions/{vid:guid}/student-sheet` and `/answer-key`, `[Authorize]` only (class-level).

- [ ] **Step 1: Write failing HTTP tests** in `ExamsHttpTests`; extend `FakeExams` with the two methods: `Capture(actor)` then return `ExamExport.BuildStudentSheet/BuildAnswerKey` over a fixed `ExportSnapshotItem` list covering all four types, whose snapshots contain the sentinels `"MODEL-ANSWER-SENTINEL"` (Essay `modelAnswer`), `"EXPLANATION-SENTINEL"` (explanation), `"ACCEPTED-SENTINEL"` (ShortAnswer `acceptedAnswers`).

```csharp
[Fact] public async Task Student_sheet_json_contains_no_answer_fields_or_values()
// GET /student-sheet → 200; walk the whole JsonElement tree: no property name in the forbidden-key list;
// raw body contains none of the three sentinels; header.versionNo == 1; items[0].number == 1.

[Fact] public async Task Answer_key_json_carries_answers_and_explanations()
// GET /answer-key → 200; body contains all three sentinels and "correctLabel"; items[*].difficulty present.

[Theory]
[InlineData("student-sheet", 404, "NotFound")] [InlineData("student-sheet", 409, "ExportUnavailable")]
[InlineData("answer-key", 403, "Forbidden")] [InlineData("answer-key", 409, "ExportUnavailable")]
public async Task Export_errors_map_to_problem_details(string route, int status, string code)
// app.Management.Error = new(status, code, "Safe error"); assert status and body "code".

[Fact] public async Task Anonymous_export_requests_never_dispatch()
// no bearer → 401 on both routes; Management.Calls == 0.
```

- [ ] **Step 2: Write failing PostgreSQL tests** in `ExamManagementTests` using existing helpers (`SeedAsync`, `SourceAsync`, `CreateAsync`, `ComposeAsync`, `ActAsync`):

```csharp
[Fact] public async Task Exports_read_frozen_snapshots_after_the_source_question_changes()
// compose q (MC Payload, correctLabel "A") → Submit (owner) → Approve (head) → Publish (admin);
// then change source: ExecuteUpdate q.Stem = "EDITED", Revision = 2 directly in DB.
// StudentSheetAsync(peer teacher same dept): stem == original stem, payload has no "correctLabel", header.SubjectName/ExamTitle set.
// AnswerKeyAsync(owner): stem == original, payload correctLabel == "A".
// AnswerKeyAsync(peer) → ExamException "Forbidden".

[Fact] public async Task Exports_refuse_editable_and_archived_versions()
// Draft (owner) → "ExportUnavailable"; Submit+Approve then Archive (owner) → "ExportUnavailable" on both;
// outsider (other department teacher) on a Submitted version → "NotFound".
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test backend/ExamMaker.slnx -c Release -m:1 --filter "FullyQualifiedName~ExamsHttpTests|FullyQualifiedName~ExamManagementTests"`
Expected: compile errors for missing interface members/routes.

- [ ] **Step 4: Implement**
  - `IExamManagement`: add the two methods.
  - `ExamManagement`: one private helper `LoadExportAsync(AuthenticatedUser actor, Guid id, bool answerKey, CancellationToken)` returning `(SheetHeader, IReadOnlyList<ExportSnapshotItem>)`: `ActorAsync(actorId, false, ct)`; `database.ExamVersions.AsNoTracking().Include(v => v.Exam).ThenInclude(e => e!.Subject).Include(v => v.Exam).ThenInclude(e => e!.Department).Include(v => v.Questions)` (no `SourceQuestion`); missing → `ExamRules.Missing()`; `ExamExport.Authorize(...)`; header from `Exam.Title`, `Subject.Name`, `Department?.Name`, `Grade`, `VersionNo`, `DurationMinutes`, `TotalPoints`; items map snapshot columns (`Type`, `Difficulty`, `StemSnapshot`, `PayloadSnapshot`, `ExplanationSnapshot`). Public methods call the builders. XML `<summary>` on each.
  - Controller: two `[HttpGet]` actions delegating with `currentUser.User!.Id`, XML summaries.

- [ ] **Step 5: Run to verify pass, then full backend suite**

Run: `dotnet test backend/ExamMaker.slnx -c Release -m:1` (requires `A2_TEST_PG_ADMIN`; see `docs/a7-exam-workflow.md`)
Expected: 0 failed, 0 skipped. Then `dotnet format backend/ExamMaker.slnx --verify-no-changes` exits 0.

---

### Task 4: Print page

**Files:**
- Create: `frontend/src/lib/exams/print.ts`, `frontend/src/lib/exams/print.test.ts`
- Create: `frontend/src/components/exams/print/sheet-header.tsx`, `student-sheet-item.tsx`, `answer-key-item.tsx`
- Create: `frontend/src/app/exams/print/page.tsx`, `frontend/src/app/exams/print/page.test.tsx`
- Modify: `frontend/src/app/layout.tsx` (header `print:hidden`; `<main>` `print:max-w-none print:p-0`), `frontend/src/app/globals.css` (`@page { size: A4; margin: 15mm; }`, `.print-item { break-inside: avoid; }`, `print-color-adjust: exact` for answer highlights), `frontend/src/messages/vi.ts` (new `exams.print` block), `frontend/.env.example` (`NEXT_PUBLIC_SCHOOL_NAME=`), `.github/workflows/deploy-frontend.yml` (env `NEXT_PUBLIC_SCHOOL_NAME: ${{ vars.NEXT_PUBLIC_SCHOOL_NAME }}`, no validation step since optional)

**Interfaces:**
- Consumes: regenerated `components["schemas"]["StudentSheet" | "AnswerKey" | "SheetHeader" | "StudentSheetItem" | "AnswerKeyItem"]`; `QuestionPayloadView` (answer mode only); `MathMarkdown`; `formatDecimalForInput`.
- Produces:
  - `type PrintMode = "student" | "answer"`
  - `parsePrintParams(search: URLSearchParams): { ok: true; vid: string; mode: PrintMode; examId: string | null } | { ok: false }`
  - `printUrl(examId: string, vid: string, mode: PrintMode): string` → `/exams/print/?id=<examId>&vid=<vid>&mode=<mode>`
  - `schoolName(): string | null` (trimmed literal env; blank → null)
  - `<SheetHeaderView header mode schoolName />`, `<StudentSheetItemView item />`, `<AnswerKeyItemView item />`

- [ ] **Step 1: Write failing tests**

```ts
// print.test.ts
expect(parsePrintParams(new URLSearchParams("vid=V&mode=student&id=E"))).toEqual({ ok: true, vid: "V", mode: "student", examId: "E" });
expect(parsePrintParams(new URLSearchParams("vid=V&mode=answer"))).toEqual({ ok: true, vid: "V", mode: "answer", examId: null });
expect(parsePrintParams(new URLSearchParams("mode=student")).ok).toBe(false);
expect(parsePrintParams(new URLSearchParams("vid=V&mode=foo")).ok).toBe(false);
expect(printUrl("E", "V", "answer")).toBe("/exams/print/?id=E&vid=V&mode=answer");
```

```ts
// page.test.tsx — mock next/navigation, auth-context, api client exactly like src/app/exams/view/page.test.tsx
it("student mode calls only student-sheet and never renders answer data even if the response carries it")
// apiGet resolves a StudentSheet whose MC item ALSO has correctLabel:"B" and explanation:"LEAK-SENTINEL" (simulated bug);
// expect every apiGet call path === "/exam-versions/{vid}/student-sheet"; screen.queryByText(/LEAK-SENTINEL/) null;
// screen.queryByText(messages.exams.print.answerLabel) null; shows "Câu 1", header title, Họ và tên line.
it("answer mode calls only answer-key and shows correct label, true/false values, accepted answers, rubric and explanation")
it("missing vid or unknown mode shows the invalid-link message and makes no API call")
it.each([[404, "notFound"], [403, "answerForbidden"], [409, "unavailable"]])("maps %i to messages.exams.print.%s")
it("print button calls window.print")
```

- [ ] **Step 2: Run to verify failure**

Run (from `frontend/`): `npx vitest run src/lib/exams/print.test.ts src/app/exams/print/page.test.tsx`
Expected: FAIL (modules not found).

- [ ] **Step 3: Implement**
  - `vi.exams.print` keys (Vietnamese): `studentTitle: "ĐỀ KIỂM TRA"`, `answerTitle: "ĐÁP ÁN VÀ HƯỚNG DẪN CHẤM"`, `department`, `subject`, `grade`, `duration` ("Thời gian làm bài"), `versionCode` ("Mã đề"), `totalPoints`, `studentName` ("Họ và tên"), `studentClass` ("Lớp"), `question` ("Câu"), `pointsUnit` ("điểm"), `answerLabel` ("Đáp án"), `explanation` ("Lời giải"), `printButton` ("In / Lưu PDF"), `toStudent` ("Xem đề học sinh"), `toAnswer` ("Xem đáp án"), `back` ("Quay lại đề"), `invalidLink`, `notFound`, `answerForbidden` ("Bạn không có quyền xem đáp án của phiên bản này."), `unavailable` ("Phiên bản này chưa thể xuất (chỉ xuất bản đã gửi duyệt, đã duyệt hoặc đã công bố).").
  - `StudentSheetItemView` reads only `item.number/points/stem` and, per `item.type`, only `options[].label/text`, `statements[].label/text` (with empty "Đúng / Sai" cells), `answerType/unit` (dotted answer line + unit), Essay → blank answer space. It must not touch any other payload key.
  - `AnswerKeyItemView` renders stem, `QuestionPayloadView`, difficulty via `vi.questions.difficulties`, explanation via `MathMarkdown`.
  - Page: `Suspense` + inner route like `/exams/view/page.tsx`; one `useQuery` with key `["examExport", mode, vid]`, `retry: false`; toolbar (`print:hidden`) with print button, link to the other mode via `printUrl` (shown always; the target page reports 403), back link `/exams/view/?id=&vid=` or `/exams/` when no `examId`.

- [ ] **Step 4: Run to verify pass**

Run: `npx vitest run src/lib/exams/print.test.ts src/app/exams/print/page.test.tsx`
Expected: PASS.

---

### Task 5: Export links on `/exams/view/`

**Files:**
- Create: `frontend/src/components/exams/export-links.tsx`
- Modify: `frontend/src/lib/exams/rules.ts` (+ `rules.test.ts`), `frontend/src/app/exams/view/page.tsx`, `frontend/src/app/exams/view/page.test.tsx`, `frontend/src/messages/vi.ts`

**Interfaces:**
- Consumes: `printUrl`, `PrintMode` (Task 4).
- Produces:
  - `canExport(status: ExamVersionStatus): boolean` — true only for `Submitted`/`Approved`/`Published`.
  - `<ExportLinks examId versionId canReadAnswers />` — student link always; answer link only when `canReadAnswers`.

- [ ] **Step 1: Write failing tests**

```ts
// rules.test.ts
expect(["Submitted", "Approved", "Published"].every(canExport)).toBe(true);
expect(["Draft", "Rejected", "Archived"].some(canExport)).toBe(false);
```
```ts
// view/page.test.tsx
it("shows both export links for an exportable version the user can read")       // hrefs equal printUrl(E1, vid, mode)
it("shows no export links for Draft, Rejected or Archived")
it("on 403 shows the student-sheet notice with a link to mode=student and no answer link")
```

- [ ] **Step 2: Run to verify failure**

Run: `npx vitest run src/lib/exams/rules.test.ts src/app/exams/view/page.test.tsx`
Expected: FAIL.

- [ ] **Step 3: Implement** — status comes from `exam.versions.find(v => v.id === vid)?.status`. When the version query succeeded and `canExport(status)`, render `<ExportLinks canReadAnswers />`. When it failed with 403 and `canExport(status)`, render `vi.exams.studentSheetOnly` plus `<ExportLinks canReadAnswers={false} />`. Update `studentSheetOnly` copy if it reads oddly next to the link.

- [ ] **Step 4: Run full frontend checks**

Run (from `frontend/`): `npm run lint && npm run typecheck && npm test && npm run api:check && npm run build`
Expected: all exit 0 (build proves `/exams/print/` works under static export).

---

### Task 6: Final verification and hand-over

**Files:**
- Create: PR description at `<scratchpad>/b10-pr-description.md` (absolute scratchpad path given by the session)

- [ ] **Step 1: Full backend + frontend runs** — repeat Task 3 Step 5 and Task 4/5 commands; record pass counts.
- [ ] **Step 2: Manual print smoke (if a local stack is available)** — open `/exams/print/?…&mode=student` and `mode=answer`, print preview A4: nav hidden, KaTeX formulas render, no answers on student sheet. If no stack, say so in the PR description.
- [ ] **Step 3: Write PR description** — English title `feat: export student sheet and answer key from exam snapshots (B10)`; Vietnamese body: tóm tắt, quyết định (Head mở rộng, chỉ Submitted/Approved/Published, whitelist), danh sách file, kết quả test thật, cách smoke test, "Closes #<issue B10>" placeholder for the user, footer `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Do **not** commit, push, or open the PR.
