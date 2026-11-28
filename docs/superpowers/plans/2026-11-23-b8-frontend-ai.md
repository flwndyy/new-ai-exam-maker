# B8 — Frontend AI + `/admin/ai-usage` — Kế hoạch triển khai

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Giáo viên sinh câu bằng AI, theo dõi job, rà soát từng câu trên giao diện; admin xem thống kê dùng AI và chi phí ước tính; backend chặn sinh câu cho môn ngoài tổ.

**Architecture:** Backend: thêm kiểm tra tổ vào `AiGenerationService` + `EfAiJobStore` (B5), thêm `AiPricingOptions` + hàm tính chi phí thuần ở Application, reader EF + controller `GET /api/v1/admin/ai-usage`. Frontend (static export): `src/lib/ai/` chứa logic thuần (backoff, hiển thị trạng thái), `src/components/ai/` chứa form/thẻ, ba trang `/ai/`, `/ai/job/`, `/admin/ai-usage/`.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit; Next.js 16 static export, TanStack Query 5, React Hook Form + Zod 4, openapi-fetch, Vitest + Testing Library.

**Spec:** `docs/superpowers/specs/2026-11-23-b8-frontend-ai-design.md`

## Global Constraints

- Nhánh `feat/frontend-ai` (đang đứng); PR vào `dev` (Squash). **Không commit/push khi người dùng chưa cho phép** (CLAUDE.md) — các bước "Commit" chỉ chạy khi đã được phép.
- Đổi quy tắc nghiệp vụ → sửa `docs/context.md` trước (Task 1 chạy đầu tiên).
- Code/tên biến/commit tiếng Anh; chuỗi UI tiếng Việt chỉ nằm trong `frontend/src/messages/vi.ts`; tài liệu `docs/` tiếng Việt.
- Frontend tương thích static export: không route `[id]`, đọc `?id=` bằng `useSearchParams`; link có dấu `/` cuối (`/ai/`, `/ai/job/?id=`, `/admin/ai-usage/`).
- Gọi API chỉ qua `api` (`@/lib/api/client`); không thêm package npm/NuGet mới.
- Nội dung AI chỉ render qua `MathMarkdown`/`QuestionPayloadView`; không `dangerouslySetInnerHTML`. Ghi chú giáo viên render text thường.
- Hằng số spec: polling 2000 → 4000 → 8000 → 15000 ms (giữ 15000), dừng ở `Succeeded`/`Failed`; gợi ý "tạm dừng" khi `Queued` > 15 phút từ `createdAt`; danh sách job refetch 15000 ms khi còn job chưa kết thúc; `note` ≤ 1000 ký tự; phân trang `/admin/ai-usage` mặc định 1/20, tối đa 100; chi phí làm tròn 4 chữ số.
- Giá mặc định (context mục 6): `global.anthropic.claude-haiku-4-5-20251001-v1:0` 1.00/5.00 USD per 1M input/output; `global.anthropic.claude-sonnet-4-6` 3.00/15.00.
- Test PostgreSQL dùng `[PostgresFact]` + `IClassFixture<PostgresFixture>`; local cần `A2_TEST_PG_ADMIN`, CI luôn chạy. Khẳng định trong test DB phải lọc theo user/ngày của test (fixture dùng chung DB).

## Review Focus

1. **Giáo viên không có tổ / admin mở `/ai/`** — `me.departmentId` null: form không gọi `fetchAllSubjects(undefined)` (sẽ ra mọi môn); hiện thông báo không có quyền sinh câu. → test ở Task 6.
2. **Job `Succeeded` nhưng một câu đã bị xóa (404)** — thẻ đó hiện "Câu này không còn khả dụng", các thẻ khác vẫn hiển thị và bộ đếm rà soát chỉ tính câu đọc được. → test ở Task 7.
3. **Poll lỗi mạng giữa chừng** — giữ dữ liệu job cũ, hiện "Mất kết nối, đang thử lại…", không về màn lỗi trắng. → test ở Task 7.
4. **Job có lần gọi model vắt qua nửa đêm UTC / job chưa chạy** — chi phí gom theo `COALESCE(finished_at, started_at, created_at)` UTC; job `Queued` token 0 không sinh dòng chi phí lỗi. → test ở Task 4.
5. **`/ai/?from=<id>` với id không tồn tại hoặc job của người khác (404)** — form vẫn dùng được với giá trị mặc định, không crash. → test ở Task 6.

---

### Task 1: Cập nhật tài liệu (context + OpenAPI)

**Files:**
- Modify: `docs/context.md` (mục 4.1 thêm một dòng; thêm "#### Chi tiết triển khai B8" sau "Chi tiết triển khai B2" ở 9.2; header lên **0.22**; "Lịch sử thay đổi" thêm **0.22 — 23/11/2026**)
- Modify: `docs/api/openapi.yaml` (`/ai/generate` description; `/admin/ai-usage` description)
- Modify: `frontend/src/lib/api/schema.d.ts` (sinh lại)

- [ ] **Step 1: Sửa `docs/context.md`**
  - 4.1: "`POST /ai/generate` từ chối (400 `errors[subjectId]`) môn không thuộc tổ hiện tại của người gửi, trước khi giữ quota; kiểm lại trong transaction tạo job."
  - "Chi tiết triển khai B8" gồm: 3 trang và quyền hiển thị; polling 2/4/8/15s, dừng khi kết thúc, không poll khi tab ẩn; cảnh báo cố định dưới ô ghi chú (tiêu chí nghiệm thu); rà soát trên trang job (`mark-reviewed` chỉ khi tác giả, AI, Draft/Rejected, chưa rà soát); `/admin/ai-usage`: dòng từ `ai_usage_daily`, chi phí từ `ai_generation_jobs` theo `model_id` và ngày UTC của `COALESCE(finished_at, started_at, created_at)`, cấu hình `AiPricing:Models:{i}:ModelId|InputPer1MUsd|OutputPer1MUsd` (danh sách vì model id chứa `:`), model không có giá → 0 + log Warning, là **ước tính** không phải hóa đơn.
- [ ] **Step 2: Sửa `docs/api/openapi.yaml`** — `/ai/generate`: thêm câu "Môn không thuộc tổ của người gửi → 400 `errors.subjectId`, không tiêu quota." `/admin/ai-usage`: thêm cách tính chi phí như Step 1. Không đổi schema.
- [ ] **Step 3: Sinh lại type và kiểm đồng bộ**
  Run: `cd frontend && npm run api:generate && npm run api:check`
  Expected: `api:check` thoát 0.
- [ ] **Step 4: Commit** — `docs: describe B8 AI frontend, usage cost estimate and department check`

---

### Task 2: Backend — chặn sinh AI cho môn ngoài tổ

**Files:**
- Modify: `backend/src/ExamMaker.Application/Ai/IAiCatalogReader.cs`
- Modify: `backend/src/ExamMaker.Infrastructure/Ai/EfAiCatalogReader.cs`
- Modify: `backend/src/ExamMaker.Application/Ai/AiGenerationService.cs`
- Modify: `backend/src/ExamMaker.Application/Ai/AiMessages.cs` (thêm hằng)
- Modify: `backend/src/ExamMaker.Infrastructure/Ai/EfAiJobStore.cs`
- Modify: `backend/src/ExamMaker.Api/Controllers/AiController.cs`
- Test: `backend/tests/ExamMaker.Application.Tests/Ai/AiGenerationServiceTests.cs`, `backend/tests/ExamMaker.Infrastructure.Tests/Ai/EfAiJobStoreTests.cs`, `backend/tests/ExamMaker.Infrastructure.Tests/Ai/EfAiCatalogReaderTests.cs`, `backend/tests/ExamMaker.Api.Tests/AiEndpointsTests.cs` (fake `InMemoryAi`)

**Interfaces:**
- Produces:
  - `IAiCatalogReader.FindSubjectDepartmentAsync(Guid subjectId, CancellationToken ct) : Task<Guid?>` — thay `SubjectExistsAsync`; null = môn không tồn tại.
  - `AiGenerationService.SubmitAsync(Guid userId, Guid? departmentId, AiGenerateRequest request, CancellationToken ct) : Task<AiSubmitResult>`
  - `AiMessages.SubjectNotInDepartment = "Môn học không thuộc tổ chuyên môn của bạn."`
  - `EfAiJobStore.TryCreateQueuedJobAsync` (chữ ký không đổi) ném `AiCatalogChangedException` khi trong transaction môn không còn cùng `department_id` với `users.department_id` của `userId`.

- [ ] **Step 1: Viết test Application (fail)** trong `AiGenerationServiceTests`: `FakeCatalog` đổi sang `Dictionary<Guid, Guid> SubjectDepartments`; mọi lời gọi hiện có truyền `DepartmentId` của `SubjectId`.
  - `Submit_SubjectOfAnotherDepartment_FailsValidationWithoutTouchingQuota`: `SubmitAsync(UserId, OtherDepartmentId, ValidRequest())` → `ValidationFailed` có key `subjectId` chứa `AiMessages.SubjectNotInDepartment`; `_jobs` không tạo job; `_queue.Enqueued` rỗng; `_usage` không được đọc.
  - `Submit_RequesterWithoutDepartment_FailsValidation`: `departmentId: null` → `ValidationFailed[subjectId]`.
  - `Submit_UnknownSubject_FailsValidationWithoutTouchingQuota` giữ nguyên thông điệp `SubjectNotFound`.
- [ ] **Step 2: Chạy, xác nhận fail**
  Run: `dotnet test backend/tests/ExamMaker.Application.Tests --filter FullyQualifiedName~AiGenerationServiceTests`
  Expected: lỗi biên dịch/FAIL ở các test mới.
- [ ] **Step 3: Cài đặt** — `ValidateCatalogAsync(request, departmentId, ct)`: `FindSubjectDepartmentAsync` null → `SubjectNotFound`; khác `departmentId` (kể cả `departmentId` null) → `SubjectNotInDepartment`. Kiểm tra topic giữ nguyên. `AiController.Generate` truyền `user.DepartmentId`. `EfAiCatalogReader.FindSubjectDepartmentAsync` select `DepartmentId`.
- [ ] **Step 4: Chạy Application tests** — cùng lệnh Step 2, Expected: PASS.
- [ ] **Step 5: Test Infrastructure (fail)** trong `EfAiJobStoreTests`:
  - `Rejects_subject_outside_requester_department_without_reserving_quota`: user tạo bằng `AiTestData.CreateUserAsync` nhưng gán vào một department **khác** department của `AiTestData.Request().SubjectId` (tạo department mới trong test) → `TryCreateQueuedJobAsync` ném `AiCatalogChangedException`; `JobsUsedAsync(userId, date) == 0`; không có job của user.
  - Các test hiện có của file chuyển sang `AiTestData.CreateSubjectUserAsync` (user cùng tổ với môn) để vẫn pass; kiểm `AiTestData.CreateQueuedJobAsync` và các helper khác dùng user cùng tổ.
  - `EfAiCatalogReaderTests`: `FindSubjectDepartmentAsync` trả đúng department của môn seed, null cho id ngẫu nhiên.
- [ ] **Step 6: Cài đặt** — trong `EfAiJobStore`, sau khóa shared 48683402, mở rộng điều kiện `AnyAsync` thành: topic khớp **và** `db.Subjects.Any(s => s.Id == request.SubjectId && db.Users.Any(u => u.Id == userId && u.DepartmentId == s.DepartmentId))`; sai → `AiCatalogChangedException` (đi nhánh có sẵn trong service).
- [ ] **Step 7: Sửa fake API** — `InMemoryAi.FindSubjectDepartmentAsync` trả `AuthTestFactory.DepartmentId` cho môn hợp lệ của test; thêm `Generate_subject_of_another_department_returns_400_subjectId` trong `AiEndpointsTests` (fake trả department khác → 400, `errors.subjectId` tồn tại, không có job được tạo).
- [ ] **Step 8: Chạy toàn bộ test backend AI**
  Run: `dotnet test backend/ExamMaker.slnx --filter "FullyQualifiedName~Ai"`
  Expected: PASS (test PostgreSQL skip nếu thiếu `A2_TEST_PG_ADMIN` — ghi rõ khi báo cáo).
- [ ] **Step 9: Commit** — `fix(ai): reject generation for subjects outside the requester's department`

---

### Task 3: Backend — bảng giá và hàm tính chi phí

**Files:**
- Create: `backend/src/ExamMaker.Application/Ai/AiPricingOptions.cs`
- Create: `backend/src/ExamMaker.Application/Ai/AiCostEstimator.cs`
- Modify: `backend/src/ExamMaker.Infrastructure/Ai/AiServiceCollectionExtensions.cs`
- Test: `backend/tests/ExamMaker.Application.Tests/Ai/AiCostEstimatorTests.cs`, `backend/tests/ExamMaker.Infrastructure.Tests/Ai/AiRegistrationTests.cs`

**Interfaces:**
- Produces:
  - `AiPricingOptions { const string SectionName = "AiPricing"; List<AiModelPrice> Models }`, `AiModelPrice { string ModelId; decimal InputPer1MUsd; decimal OutputPer1MUsd }`, `static IReadOnlyList<AiModelPrice> AiPricingOptions.Defaults` (2 model ở Global Constraints).
  - `AiCostEstimator(AiPricingOptions options)` — ctor ném `InvalidOperationException` khi giá âm, `ModelId` rỗng hoặc trùng (so sánh ordinal).
  - `decimal AiCostEstimator.Estimate(string? modelId, long inputTokens, long outputTokens, out bool priced)` — `priced=false` và trả 0 khi `modelId` null/không có giá.
  - DI: singleton `AiPricingOptions` + singleton `AiCostEstimator`.

- [ ] **Step 1: Viết test (fail)** — `AiCostEstimatorTests`:
  - `Haiku_cost_uses_input_and_output_prices`: 1_000_000 in + 1_000_000 out → `6.00m`, `priced` true.
  - `Sonnet_cost`: 4000 in + 4000 out → `0.072m`.
  - `Unknown_or_null_model_costs_zero_and_is_unpriced`.
  - `Negative_price_or_duplicate_model_id_throws`.
  - `AiRegistrationTests`: không cấu hình → estimator dùng `Defaults` (đúng 2 model); có `AiPricing:Models:0:ModelId=x`… → **chỉ** model cấu hình (không cộng dồn vào mặc định).
- [ ] **Step 2: Chạy, xác nhận fail** — `dotnet test backend/tests/ExamMaker.Application.Tests --filter FullyQualifiedName~AiCostEstimatorTests`
- [ ] **Step 3: Cài đặt** — `Models` mặc định **rỗng** khi bind; trong `AddExamMakerAi`, sau `Bind`, nếu `Models.Count == 0` thì gán `Defaults` (tránh binder .NET nối danh sách cấu hình vào danh sách mặc định). Tính bằng `decimal`: `in/1_000_000m × InputPer1MUsd + out/1_000_000m × OutputPer1MUsd`, không làm tròn ở đây.
- [ ] **Step 4: Chạy test** — Application + `dotnet test backend/tests/ExamMaker.Infrastructure.Tests --filter FullyQualifiedName~AiRegistrationTests`, Expected: PASS.
- [ ] **Step 5: Commit** — `feat(ai): add configurable model pricing and cost estimator`

---

### Task 4: Backend — `GET /api/v1/admin/ai-usage`

**Files:**
- Create: `backend/src/ExamMaker.Application/Ai/AiUsageReport.cs` (DTO + interface + queries)
- Create: `backend/src/ExamMaker.Infrastructure/Ai/EfAiUsageReportReader.cs`
- Create: `backend/src/ExamMaker.Api/Controllers/AdminAiUsageController.cs`
- Modify: `backend/src/ExamMaker.Infrastructure/Ai/AiServiceCollectionExtensions.cs`
- Test: `backend/tests/ExamMaker.Application.Tests/Ai/AiUsageReportQueriesTests.cs`, `backend/tests/ExamMaker.Infrastructure.Tests/Ai/EfAiUsageReportReaderTests.cs`, `backend/tests/ExamMaker.Api.Tests/AdminAiUsageTests.cs`

**Interfaces:**
- Consumes: `AiCostEstimator` (Task 3), `AiUserRef` (có sẵn).
- Produces (khớp schema `AiUsageRow`/`AiUsagePage` trong OpenAPI):
  - `record AiUsageRow(AiUserRef User, DateOnly Date, int Jobs, long Tokens, decimal EstimatedCostUsd)`
  - `record AiUsageTotals(long Jobs, long Tokens, decimal EstimatedCostUsd)`
  - `record AiUsagePage(IReadOnlyList<AiUsageRow> Items, int Page, int PageSize, int TotalItems, int TotalPages, AiUsageTotals Totals)`
  - `record AiUsageFilter(Guid? UserId, DateOnly? From, DateOnly? To, int Page, int PageSize)`
  - `interface IAiUsageReportReader { Task<AiUsagePage> ReadAsync(AiUsageFilter filter, CancellationToken ct); }`
  - `AiUsageReportQueries.ListAsync(int page, int pageSize, Guid? userId, string? from, string? to, CancellationToken ct) : Task<AiUsageReportResult>` với `AiUsageReportResult.Ok(AiUsagePage)` / `.Invalid(IReadOnlyDictionary<string,string[]>)` (theo khuôn `AiJobListResult`).
  - Controller `api/v1/admin/ai-usage`, `[Authorize(Policy = AccessPolicies.Admin)]`, `Invalid` → `ValidationProblem` (khuôn `AiController.ValidationProblemFor`).

- [ ] **Step 1: Test validate (fail)** — `AiUsageReportQueriesTests` với fake reader: `page=0` → `errors.page`; `pageSize=101` → `errors.pageSize`; `from="2026-13-01"` hoặc `"2026/11/01"` → `errors.from` (chỉ nhận đúng `yyyy-MM-dd`, `CultureInfo.InvariantCulture`); `from > to` → `errors.to`; hợp lệ → reader nhận đúng `AiUsageFilter`.
- [ ] **Step 2: Cài đặt `AiUsageReportQueries`**, chạy test → PASS.
- [ ] **Step 3: Test reader PostgreSQL (fail)** — `EfAiUsageReportReaderTests`, dữ liệu do test tự chèn (user riêng, ngày `AiTestData.UniqueDate()`), luôn lọc `UserId` của test:
  - `Rows_come_from_usage_daily_sorted_by_date_desc_with_user_ref`.
  - `Cost_sums_jobs_by_model_on_cost_day`: 2 job cùng ngày — Haiku 4000/4000 và Sonnet 4000/4000 (`finished_at` cùng ngày) → `EstimatedCostUsd == 0.0960m` (0.024 + 0.072).
  - `Job_created_before_midnight_finished_after_counts_on_finish_day`: `created_at` D 15:59Z, `finished_at` D+1 00:05Z → chi phí ở dòng D+1, dòng D = 0.
  - `Queued_job_without_model_adds_no_cost`.
  - `Unknown_model_costs_zero`.
  - `Totals_cover_all_filtered_rows_not_only_page`: 3 ngày, `pageSize=1` → `Items.Count==1`, `TotalItems==3`, `Totals` = tổng 3 ngày.
  - `From_and_to_are_inclusive`.
- [ ] **Step 4: Cài đặt `EfAiUsageReportReader(ExamMakerDbContext db, AiCostEstimator cost, ILogger<EfAiUsageReportReader> logger)`**
  - Dòng + `TotalItems` + `Totals.Jobs/Tokens`: LINQ trên `db.AiUsageDaily` join `Users`, `OrderByDescending(Date).ThenBy(UserId)`, `Skip/Take`.
  - Chi phí: **SQL thô** (tránh dịch timezone của `.Date`), gom theo người, ngày chi phí, model:
    ```sql
    SELECT requested_by AS "UserId",
           (COALESCE(finished_at, started_at, created_at) AT TIME ZONE 'UTC')::date AS "Day",
           model_id AS "ModelId",
           SUM(input_tokens)::bigint AS "InputTokens", SUM(output_tokens)::bigint AS "OutputTokens"
    FROM ai_generation_jobs
    WHERE (@userId IS NULL OR requested_by = @userId)
      AND (COALESCE(finished_at, started_at, created_at) AT TIME ZONE 'UTC')::date BETWEEN @from AND @to
      AND (input_tokens > 0 OR output_tokens > 0)
    GROUP BY 1, 2, 3
    ```
    (`@from`/`@to` không có thì dùng `DateOnly.MinValue`/`MaxValue` hoặc bỏ điều kiện.) Dùng `db.Database.SqlQuery<…>`. Chi phí dòng = tổng nhóm có (UserId, Day) trùng dòng; `Totals.EstimatedCostUsd` = tổng mọi nhóm. Model chưa có giá → log `Warning` một lần mỗi request, liệt kê các model id. Làm tròn `Math.Round(x, 4, MidpointRounding.AwayFromZero)` khi dựng DTO.
- [ ] **Step 5: Chạy reader test** — `dotnet test backend/tests/ExamMaker.Infrastructure.Tests --filter FullyQualifiedName~EfAiUsageReportReaderTests`, Expected: PASS.
- [ ] **Step 6: Test HTTP + controller** — `AdminAiUsageTests` (khuôn `AdminUsersTests`/`AiEndpointsTests`, fake `IAiUsageReportReader`): admin → 200 với shape `items[].user.fullName`, `items[].estimatedCostUsd`, `totals`; teacher, head → 403; không token → 401; `pageSize=500` → 400 `errors.pageSize`. Tạo controller, đăng ký DI (`IAiUsageReportReader` → `EfAiUsageReportReader`, `AiUsageReportQueries` scoped). Chạy `dotnet test backend/tests/ExamMaker.Api.Tests --filter FullyQualifiedName~AdminAiUsageTests` → PASS.
- [ ] **Step 7: Commit** — `feat(admin): add AI usage report endpoint with estimated cost`

---

### Task 5: Frontend — nền tảng AI (backoff, nhãn trạng thái, chuỗi, menu)

**Files:**
- Create: `frontend/src/lib/ai/polling.ts`, `frontend/src/lib/ai/job-status.ts`
- Modify: `frontend/src/messages/vi.ts` (thêm khóa `ai`, `aiUsage`, `nav.ai`, `nav.aiUsage`)
- Modify: `frontend/src/components/role-nav.tsx`, `frontend/src/app/admin/page.tsx` (link sang `/admin/ai-usage/`)
- Test: `frontend/src/lib/ai/polling.test.ts`, `frontend/src/lib/ai/job-status.test.ts`, `frontend/src/components/role-nav.test.tsx`

**Interfaces:**
- Produces:
  - `POLL_DELAYS_MS = [2000, 4000, 8000, 15000] as const`
  - `isJobFinished(status: AiJobStatus): boolean`
  - `nextPollDelay(pollCount: number, status: AiJobStatus | undefined): number | false` — `false` khi kết thúc; `undefined` (chưa có dữ liệu) → 2000; `pollCount` ≥ 3 → 15000.
  - `STALE_QUEUED_MS = 15 * 60 * 1000`
  - `type JobDisplay = { kind: "waiting" | "retrying" | "running" | "failed" | "succeeded"; attempt: number; maxAttempts: number; stale: boolean; message: string | null }`
  - `describeJob(job: AiJob, now: Date): JobDisplay` — `Queued`+`attempt=0` → `waiting`; `Queued`+`attempt>0` → `retrying` (message = `errorMessage`); `stale` = `Queued` và `now - createdAt > STALE_QUEUED_MS`; `Failed` → message = `errorMessage ?? vi.ai.failedFallback`.
  - `vi.ai.noteWarning` = "Không nhập thông tin cá nhân của học sinh/giáo viên và không dán nguyên văn tài liệu có bản quyền (SGK, đề thương mại). Nội dung ghi chú được gửi tới dịch vụ AI và có thể được xử lý ngoài Việt Nam/Singapore."
  - `vi.aiUsage.estimateNotice` = "Chi phí là ƯỚC TÍNH theo bảng giá cấu hình, không phải hóa đơn AWS."

- [ ] **Step 1: Test (fail)** — `polling.test.ts`: dãy `nextPollDelay(0..5,"Queued")` = `[2000,4000,8000,15000,15000,15000]`; `"Running"` như vậy; `"Succeeded"`/`"Failed"` → `false`. `job-status.test.ts`: 5 nhánh của `describeJob` + `stale` đúng ở 15 phút + 1 ms, sai ở đúng 15 phút. `role-nav.test.tsx`: teacher/head thấy link `/ai/` (không thấy `/admin/ai-usage/`); admin thấy `/admin/ai-usage/` (không thấy `/ai/`).
- [ ] **Step 2: Chạy** `cd frontend && npx vitest run src/lib/ai src/components/role-nav.test.tsx` → FAIL.
- [ ] **Step 3: Cài đặt** các hàm + chuỗi + link menu.
- [ ] **Step 4: Chạy lại** → PASS; `npm run typecheck` → 0 lỗi.
- [ ] **Step 5: Commit** — `feat(frontend): add AI polling helpers, job status mapping and navigation`

---

### Task 6: Frontend — trang `/ai/` (form sinh câu + job của tôi)

**Files:**
- Create: `frontend/src/app/ai/page.tsx`, `frontend/src/components/ai/generate-form.tsx`, `frontend/src/components/ai/job-list.tsx`, `frontend/src/lib/ai/generate-schema.ts`
- Test: `frontend/src/app/ai/page.test.tsx`, `frontend/src/lib/ai/generate-schema.test.ts`

**Interfaces:**
- Consumes: `nextPollDelay`/`isJobFinished` (Task 5), `fetchAllSubjects`, `fetchAllTopics`, `getApiErrorCode`, `getApiErrorMessage`.
- Produces:
  - `makeGenerateSchema(maxQuestionsPerJob: number)` (Zod) cho `{ subjectId, grade, topicId, type, difficulty, count, note }`; `note` trim, rỗng → `null`, tối đa 1000.
  - `GenerateForm({ initial?: Partial<AiGenerateRequest> })` — điền sẵn khi có `initial`.
  - Trang đọc `?from=<jobId>`: `GET /ai/jobs/{id}` thành công → `initial = job.params`; lỗi → bỏ qua.

- [ ] **Step 1: Test (fail)** — theo khuôn mock của `src/app/a10-screens.test.tsx`:
  - `shows the note warning without focusing the note field`: render, `getByText(messages.ai.noteWarning)` có mặt ngay; textarea có `aria-describedby` trỏ tới phần tử cảnh báo.
  - `loads only subjects of the user's department`: `apiGet` được gọi `/subjects` với `departmentId = me.departmentId`.
  - `user without department sees no-permission message and no subject request` (Review Focus 1); admin cũng vậy.
  - `count above quota max is rejected by the form` (quota `maxQuestionsPerJob: 10`, nhập 11 → lỗi field, không gọi POST).
  - `submits and navigates to the job page`: POST `/ai/generate` trả job id → `router.push("/ai/job/?id=<id>")`.
  - `429 QuotaExceeded shows quota message`; `429 RateLimited shows retry message`; `400 errors.subjectId is shown under the subject field`.
  - `disables submit when jobsUsed >= jobsLimit`.
  - `prefills from ?from=<id>`; `?from=<id> returning 404 still renders an empty usable form` (Review Focus 5).
  - `job list shows status badges and links to /ai/job/?id=`.
  - `generate-schema.test.ts`: note 1001 ký tự lỗi, 1000 hợp lệ; note "   " → `null`.
- [ ] **Step 2: Chạy** `npx vitest run src/app/ai/page.test.tsx src/lib/ai/generate-schema.test.ts` → FAIL.
- [ ] **Step 3: Cài đặt** — trang bọc nội dung client trong `Suspense` (do `useSearchParams`), như các trang query-string hiện có. Môn → khối → chương lọc dây chuyền, đổi môn/khối xóa chương. Quota từ `GET /ai/quota`, `resetsAt` hiển thị giờ `Asia/Ho_Chi_Minh`. Thành công → invalidate `["ai","quota"]` và `["ai","jobs"]`. Danh sách job: `GET /ai/jobs` phân trang 20, `refetchInterval` = 15000 khi có item chưa kết thúc, ngược lại `false`.
- [ ] **Step 4: Chạy lại** → PASS; `npm run lint && npm run typecheck` → sạch.
- [ ] **Step 5: Commit** — `feat(frontend): add AI generation form and job list`

---

### Task 7: Frontend — trang `/ai/job/` (polling + rà soát)

**Files:**
- Create: `frontend/src/app/ai/job/page.tsx`, `frontend/src/components/ai/job-status-panel.tsx`, `frontend/src/components/ai/generated-question-card.tsx`
- Test: `frontend/src/app/ai/job/page.test.tsx`

**Interfaces:**
- Consumes: `nextPollDelay`, `describeJob` (Task 5); `MathMarkdown`, `QuestionPayloadView`; `vi.questions.aiFlags`, `vi.questions.statuses` (có sẵn).
- Produces:
  - `canMarkReviewed(question: Question, meId: string): boolean` = tác giả && `source === "AI"` && `!aiReviewedByAuthor` && status ∈ {`Draft`, `Rejected`} (export từ `generated-question-card.tsx`).
  - `GeneratedQuestionCard({ questionId, index, meId })`.

- [ ] **Step 1: Test (fail)** — dùng `vi.useFakeTimers()` cho polling:
  - `polls with backoff and stops when succeeded`: lần 1 `Queued`, lần 2 `Running`, lần 3 `Succeeded` → số lần gọi `GET /ai/jobs/{id}` là 3 sau khi tiến 2000+4000 ms; tiến thêm 60000 ms không gọi thêm.
  - Mỗi trạng thái hiển thị đúng chuỗi: chờ, chờ thử lại "lần 1/3" + `errorMessage`, gợi ý tạm dừng khi `createdAt` cũ hơn 15 phút, đang chạy, `Failed` có `errorMessage` và link `/ai/?from=<id>`.
  - `Succeeded with zero questions shows all-duplicates notice and regenerate link`.
  - `renders one card per questionId in order, with AI flag and summary counts` ("Đã rà soát 1/2", `duplicatesDropped`).
  - `Disagree flag shows warning and verification note`.
  - `mark reviewed posts to /questions/{id}/mark-reviewed and updates counter`.
  - `no mark-reviewed button for Submitted question or non-author`.
  - `deleted question (404) shows unavailable card, others still render` (Review Focus 2).
  - `network error during polling keeps last job and shows reconnecting notice` (Review Focus 3).
  - `job 404 shows not found`.
  - `stem with <script> and <img onerror> renders no script/img element; $x^2$ renders .katex` (sanitize).
  - `teacher note renders as plain text` (`**x**` hiện nguyên văn, không có `<strong>`).
- [ ] **Step 2: Chạy** `npx vitest run src/app/ai/job/page.test.tsx` → FAIL.
- [ ] **Step 3: Cài đặt** — `useQuery(["ai","job",id])` với `refetchInterval: (query) => nextPollDelay(query.state.dataUpdateCount + query.state.errorUpdateCount - 1, query.state.data?.status)`; lỗi khi đã có `data` → giữ `data`, hiện thông báo mất kết nối. Khi `Succeeded`: `useQueries` mỗi `questionIds` → `GET /questions/{id}`; `mark-reviewed` thành công → `setQueryData` câu đó (hoặc invalidate). Nút "Sửa" → `/questions/detail/?id=<id>`. Ghi chú render trong `<p className="whitespace-pre-wrap">`.
- [ ] **Step 4: Chạy lại** → PASS; lint + typecheck sạch.
- [ ] **Step 5: Commit** — `feat(frontend): add AI job page with polling and per-question review`

---

### Task 8: Frontend — trang `/admin/ai-usage/`

**Files:**
- Create: `frontend/src/app/admin/ai-usage/page.tsx`
- Test: `frontend/src/app/admin/ai-usage/page.test.tsx`

**Interfaces:**
- Consumes: `GET /admin/ai-usage`, `GET /admin/users` (gom mọi trang, pageSize 100), `vi.aiUsage.*`.

- [ ] **Step 1: Test (fail)**:
  - `non-admin sees no-permission message and no request`.
  - `renders rows, totals and estimate notice` (định dạng `vi-VN`, chi phí 4 chữ số, cột ghi "(UTC)").
  - `filter sends userId/from/to query` khi bấm "Lọc".
  - `pagination requests next page`.
  - `400 shows validation message`.
- [ ] **Step 2: Chạy** `npx vitest run src/app/admin/ai-usage/page.test.tsx` → FAIL.
- [ ] **Step 3: Cài đặt** theo phác thảo spec 5.6; input ngày `type="date"`; bộ lọc chỉ áp khi bấm "Lọc" (reset về trang 1).
- [ ] **Step 4: Chạy lại** → PASS.
- [ ] **Step 5: Commit** — `feat(frontend): add admin AI usage page`

---

### Task 9: Kiểm tra toàn bộ

- [ ] **Step 1: Frontend** — `cd frontend && npm run lint && npm run typecheck && npm test && npm run build && npm run api:check`. Expected: tất cả thoát 0; `out/ai/index.html`, `out/ai/job/index.html`, `out/admin/ai-usage/index.html` tồn tại.
- [ ] **Step 2: Backend** — `dotnet format backend/ExamMaker.slnx --verify-no-changes && dotnet test backend/ExamMaker.slnx`. Expected: PASS; nếu không có `A2_TEST_PG_ADMIN` thì ghi rõ số test PostgreSQL bị skip, cần CI xác nhận.
- [ ] **Step 3: Đối chiếu spec** — cảnh báo ghi chú luôn hiển thị; không còn `dangerouslySetInnerHTML` mới (`git diff dev -- frontend | grep dangerouslySetInnerHTML` rỗng); `docs/context.md` có "Chi tiết triển khai B8" và lịch sử 0.22.
- [ ] **Step 4: Báo cáo** kết quả cho người dùng; chờ cho phép trước khi push/mở PR.
