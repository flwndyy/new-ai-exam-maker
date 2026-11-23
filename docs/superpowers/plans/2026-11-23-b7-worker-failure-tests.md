# B7 — Test lỗi đầu–cuối cho Worker AI — Kế hoạch triển khai

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chứng minh các bảo đảm của worker AI (mục 4.1.1) khi ghép thật: giao trùng message, crash giữa chừng, retry/DLQ, vượt quota — trên PostgreSQL thật, Bedrock giả, SQS giả trong bộ nhớ.

**Architecture:** Chỉ thêm test trong `ExamMaker.Infrastructure.Tests`. Một hàng đợi giả `FakeSqsQueue` mô phỏng receive count / visibility / redrive DLQ theo `FakeTimeProvider`; một `WorkerHarness` dựng `AiJobMessageHandler` + `AiJobProcessor` + `EfAiJobWorkStore` + `AiJobRecovery` thật, mỗi lần xử lý một `DbContext` riêng (như một worker). Không sửa code worker; nếu test lộ bug thì sửa trong cùng PR và ghi vào mô tả PR.

**Tech Stack:** .NET 10, xUnit 2.9, EF Core + Npgsql, `Microsoft.Extensions.TimeProvider.Testing` (đã có trong csproj).

**Spec:** `docs/superpowers/specs/2026-11-23-b7-worker-failure-tests-design.md`

## Global Constraints

- Nhánh `feat/b7-worker-failure-tests` tách từ `dev`; PR vào `dev` (Squash). **Không commit/push khi người dùng chưa cho phép** (CLAUDE.md) — các bước "Commit" dưới đây chỉ chạy khi đã được phép.
- Mọi test cần DB dùng `[PostgresFact]` + `IClassFixture<PostgresFixture>`; chạy local cần biến `A2_TEST_PG_ADMIN` (PostgreSQL tạm có quyền CREATEDB), CI luôn chạy.
- Không gọi Bedrock/SQS thật; không thêm package mới.
- Hằng số khớp spec: `maxReceiveCount = 3`, visibility hàng đợi `300s`, backoff `30s / 120s / 300s`, `lock_timeout 2s`, `RequeueAfterMinutes = 10`.
- Code/tên test tiếng Anh; tài liệu tiếng Việt.
- Fixture dùng chung DB cho cả class và `AiJobRecovery` quét **toàn bộ** job: mọi khẳng định phải lọc theo `jobId` của test (không đếm tổng bảng, không `Assert.Single` trên toàn hàng đợi mà không lọc).
- Ngày quota lấy từ `FakeTimeProvider`: mỗi harness khởi tạo đồng hồ ở `AiTestData.UniqueDate()` lúc 08:00 UTC.

## Review Focus

1. **Treo test vì khóa sai loại**: giữ hàng `users` bằng `FOR UPDATE` sẽ chặn cả `INSERT ai_usage_daily` (FK lấy `FOR KEY SHARE`, không có lock_timeout) → treo mãi. Phải dùng `FOR NO KEY UPDATE` (chặn `FOR SHARE` của `CompleteAsync`, không chặn FK). Test C1 đặt `Timeout = 60_000`.
2. **Chạy song song không tất định**: ở D2, worker 2 phải chạy **sau khi** worker 1 đã claim và đang chặn trong model; chờ tín hiệu `Entered` của gate, không dùng `Task.Delay`. Test đặt `Timeout = 60_000`.
3. **Xóa message lỗi sau commit** (commit xong nhưng chết trước khi xóa) → message quay lại không được sinh trùng câu: test D3.
4. **Worker "zombie" ghi sau khi mất lease** không được ghi đè kết quả của worker mới: khẳng định cuối test C2.
5. **Bước kiểm chứng nuốt mọi lỗi model** nên một lần gọi thừa ngoài kịch bản sẽ bị che thành `NotChecked`: model giả ném `InvalidOperationException` khi hết kịch bản và mọi test khẳng định `model.Calls` chính xác.

---

## File Structure

- Create `backend/tests/ExamMaker.Infrastructure.Tests/Ai/Worker/FakeSqsQueue.cs` — `FakeSqsQueue`, `FakeSqsMessage`.
- Create `backend/tests/ExamMaker.Infrastructure.Tests/Ai/Worker/ScriptedModel.cs` — model giả theo kịch bản + gate.
- Create `backend/tests/ExamMaker.Infrastructure.Tests/Ai/Worker/WorkerHarness.cs` — ghép thành phần thật, thao tác DB phụ trợ, đọc trạng thái job.
- Create `backend/tests/ExamMaker.Infrastructure.Tests/Ai/Worker/FakeSqsQueueTests.cs` — test thuần cho hàng đợi giả.
- Create `.../Ai/Worker/WorkerDuplicateDeliveryTests.cs`, `WorkerRetryDlqTests.cs`, `WorkerCrashTests.cs`, `WorkerQuotaTests.cs`.
- Modify `backend/tests/ExamMaker.Infrastructure.Tests/Ai/AiTestData.cs` — thêm `ModelOutput(int count)` (chuyển từ `AiJobProcessorPostgresTests.Output`).
- Modify `backend/tests/ExamMaker.Infrastructure.Tests/Ai/AiJobProcessorPostgresTests.cs` — dùng `AiTestData.ModelOutput`.

Namespace cho file mới: `ExamMaker.Infrastructure.Tests.Ai.Worker`.

Lệnh chạy chung (từ `backend/`): `dotnet test ExamMaker.slnx --filter "FullyQualifiedName~ExamMaker.Infrastructure.Tests.Ai.Worker"`

---

### Task 1: Hàng đợi giả, model giả và harness

**Files:**
- Create: `FakeSqsQueue.cs`, `ScriptedModel.cs`, `WorkerHarness.cs`, `FakeSqsQueueTests.cs` (thư mục `Ai/Worker/`)
- Modify: `Ai/AiTestData.cs`, `Ai/AiJobProcessorPostgresTests.cs`

**Interfaces:**
- Consumes (code có sẵn): `AiJobMessageHandler(AiJobProcessor, IAiJobQueue, ILogger<…>)`, `HandleAsync(IAiQueueMessage, CancellationToken)`; `AiJobProcessor(IAiJobWorkStore, IAiDuplicateKeySource, IAiModelClient, IAiUsageReader, AiQuotaOptions, AiGenerationOptions, AiWorkerOptions, TimeProvider, ILogger<…>)`; `EfAiJobWorkStore(ExamMakerDbContext)` (cài cả `IAiJobRecoveryStore`); `EfAiDuplicateKeySource(db)`, `EfAiUsageReader(db)`; `AiJobRecovery(IAiJobRecoveryStore, IAiJobQueue, AiWorkerOptions, ILogger<…>).RunOnceAsync(ct)`; `AiModelResponse(string Text, AiStopReason, int InputTokens, int OutputTokens, string ModelId)`; `AiModelException(AiModelErrorKind, string)`.
- Produces:
  - `AiTestData.ModelOutput(int count) : string` — JSON `{"questions":[…]}` gồm `count` câu MultipleChoice hợp lệ, stem có tag GUID riêng mỗi lần gọi (tránh lọc trùng giữa các test).
  - `sealed class FakeSqsQueue(TimeProvider time, int maxReceiveCount = 3) : IAiJobQueue`
    - `Task EnqueueAsync(Guid jobId, CancellationToken ct)` — thêm message body `{"jobId":"<guid>"}`, `ReceiveCount = 0`, hiện ngay.
    - `IReadOnlyList<FakeSqsMessage> Receive()` — với mỗi message trong hàng đợi đã hiện (`VisibleAt <= now`): nếu `ReceiveCount >= maxReceiveCount` → chuyển sang `Dlq`, không trả về; ngược lại `ReceiveCount++`, `VisibleAt = now + 300s`, trả về.
    - `IReadOnlyList<FakeSqsMessage> InQueue`, `IReadOnlyList<FakeSqsMessage> Dlq`; `IEnumerable<FakeSqsMessage> For(Guid jobId)` (trong hàng đợi).
    - `int FailNextDeletes { get; set; }` — số lần `DeleteAsync` kế tiếp sẽ ném `InvalidOperationException`.
  - `sealed class FakeSqsMessage : IAiQueueMessage` — `Guid JobId`, `string Body`, `int ReceiveCount`, `DateTimeOffset VisibleAt`, `bool Deleted`, `List<TimeSpan> VisibilityChanges`; `ChangeVisibilityAsync(t)` ghi lại `t` và đặt `VisibleAt = now + t`; `DeleteAsync` gỡ khỏi hàng đợi, `Deleted = true` (hoặc ném theo `FailNextDeletes`).
  - `sealed class ScriptedModel : IAiModelClient` — `ScriptedModel(params Func<AiModelRequest, Task<AiModelResponse>>[] steps)`; `int Calls`; hết kịch bản thì ném `InvalidOperationException("Unexpected model call")`. Bước dựng sẵn (static): `Valid(int count)` (trả `AiTestData.ModelOutput(count)`, token 10/10, `EndTurn`, model `"fake-model"`), `Fail(AiModelErrorKind kind)`, `Gated(ModelGate gate, int count)` (báo `gate.Entered`, chờ `gate.Release` rồi trả `Valid`).
  - `sealed class ModelGate` — `Task Entered`, `void Release()`.
  - `sealed class WorkerHarness(PostgresFixture fixture)`:
    - `FakeTimeProvider Time` (khởi tạo `UniqueDate()` 08:00 UTC), `DateOnly Today`, `FakeSqsQueue Queue`.
    - `AiQuotaOptions Quota { get; } = new()`, `AiGenerationOptions Generation { get; } = new()` — test chỉnh trước khi chạy.
    - `Task<(Guid UserId, Guid JobId)> CreateJobAsync(bool enqueue = true)` — `AiTestData.CreateSubjectUserAsync` + `CreateQueuedJobAsync`, rồi `Queue.EnqueueAsync` nếu `enqueue`.
    - `Task HandleAsync(FakeSqsMessage message, IAiModelClient model)` — `DbContext` mới, ghép thật như trên, `AiWorkerOptions` mặc định, `CancellationToken.None`.
    - `Task<int> DrainAsync(IAiModelClient model)` — lặp: `Receive()`; nếu rỗng mà còn message trong hàng đợi thì `Time.Advance` tới `VisibleAt` sớm nhất; xử lý tuần tự từng message; dừng khi hàng đợi rỗng hoặc sau 10 vòng; trả số lần xử lý.
    - `Task<AiJobClaim> CrashAfterClaimAsync(Guid jobId)` — `DbContext` mới, `EfAiJobWorkStore.TryClaimAsync(jobId, 300s, AiPromptBuilder.Version, None)`, khẳng định khác null, không làm gì thêm (worker chết).
    - `Task ExpireLeaseAsync(Guid jobId)` — `UPDATE ai_generation_jobs SET lease_expires_at = now() - interval '1 second' WHERE id = …`.
    - `Task AgeLastEnqueuedAsync(Guid jobId, TimeSpan by)` — lùi `last_enqueued_at`.
    - `Task RunRecoveryAsync()` — `DbContext` mới, `AiJobRecovery(store, Queue, new AiWorkerOptions(), NullLogger)`.
    - `Task SetUsageAsync(Guid userId, long tokens)` — upsert `ai_usage_daily (user_id, Today, 0, tokens)`.
    - `Task<long> UsageTokensAsync(Guid userId)` — token của `Today`.
    - `Task<JobState> ReadJobAsync(Guid jobId)` với `sealed record JobState(string Status, int Attempt, string? ErrorCode, string? ErrorMessage, long InputTokens, long OutputTokens, IReadOnlyList<int> Ordinals, IReadOnlyList<string> Verifications, IReadOnlyList<string> JobAuditNotes)` — `Ordinals`/`Verifications` từ `questions WHERE ai_job_id = jobId` sắp theo ordinal; `JobAuditNotes` từ `audit_logs WHERE entity_type='AiJob' AND entity_id=jobId` sắp theo `created_at`.

- [ ] **Step 1: Tạo nhánh**

Run: `git switch dev && git pull && git switch -c feat/b7-worker-failure-tests`

- [ ] **Step 2: Viết test thất bại cho hàng đợi giả** (`FakeSqsQueueTests`, `[Fact]` thuần, không DB)

```csharp
[Fact] public async Task Receive_hides_message_for_300_seconds_and_counts_receives()
// Enqueue → Receive() trả 1, ReceiveCount==1; Receive() ngay sau trả rỗng;
// Time.Advance(300s) → Receive() trả lại cùng message, ReceiveCount==2.

[Fact] public async Task Fourth_receive_moves_message_to_dlq()
// 3 vòng Receive+Advance(300s) → vòng 4: Receive() rỗng, Dlq.Count==1, InQueue rỗng.

[Fact] public async Task Change_visibility_and_delete_apply_to_the_queue()
// ChangeVisibilityAsync(30s) → VisibilityChanges==[30s]; Advance(30s) → nhận lại được;
// DeleteAsync → InQueue rỗng, Deleted==true.

[Fact] public async Task FailNextDeletes_throws_and_keeps_the_message()
```

- [ ] **Step 3: Chạy, xác nhận FAIL** (biên dịch lỗi vì chưa có `FakeSqsQueue`).

- [ ] **Step 4: Cài `FakeSqsQueue`, `FakeSqsMessage`, `ScriptedModel`, `ModelGate`, `WorkerHarness`, `AiTestData.ModelOutput`; đổi `AiJobProcessorPostgresTests` sang dùng `AiTestData.ModelOutput`.**

- [ ] **Step 5: Chạy `FakeSqsQueueTests` và `AiJobProcessorPostgresTests` → PASS.**

- [ ] **Step 6: Commit** — `test(worker): add fake SQS queue and end-to-end worker harness`

---

### Task 2: Giao trùng message *(MVP)*

**Files:** Create `Ai/Worker/WorkerDuplicateDeliveryTests.cs`

**Interfaces:** Consumes toàn bộ Produces của Task 1.

- [ ] **Step 1: Viết test** (`[PostgresFact]`, `IClassFixture<PostgresFixture>`)

```csharp
D1 Duplicate_message_after_success_is_deleted_without_new_work
// CreateJobAsync(); Queue.EnqueueAsync(jobId) lần 2 (2 message cùng jobId).
// model = new ScriptedModel(ScriptedModel.Valid(5)); DrainAsync(model).
// Assert: model.Calls==1; state.Status=="Succeeded"; Ordinals==[1..5];
//   JobAuditNotes chứa đúng 1 note bắt đầu "AI worker; questions=5";
//   InputTokens==10 && OutputTokens==10; UsageTokensAsync==20; Queue.For(jobId) rỗng; Dlq rỗng.

D2 Two_workers_on_the_same_job_produce_one_result   [PostgresFact(Timeout = 60_000)]
// 2 message cùng jobId; var gate = new ModelGate();
// m = Queue.Receive() (2 message). w1 = HandleAsync(m[0], new ScriptedModel(Gated(gate,5)));
// await gate.Entered; await HandleAsync(m[1], new ScriptedModel())  // model rỗng: gọi là lỗi
// Assert sau w2: m[1].VisibilityChanges.Single() trong (0, 300s]; m[1].Deleted==false.
// gate.Release(); await w1; Assert m[0].Deleted.
// DrainAsync(new ScriptedModel()) → message còn lại bị xóa (job Finished).
// Assert: Status Succeeded, Attempt==1, Ordinals==[1..5], 1 note Succeeded, UsageTokensAsync==20.

D3 Delete_failure_after_commit_redelivers_without_duplicating
// Queue.FailNextDeletes = 1; DrainAsync(new ScriptedModel(Valid(5))).
// Assert: model.Calls==1; Ordinals==[1..5]; 1 note Succeeded; Queue.For(jobId) rỗng; Dlq rỗng.
```

- [ ] **Step 2: Chạy → PASS** (B6 đã cài hành vi; nếu FAIL là bug thật → dừng, báo người dùng trước khi sửa code worker).

- [ ] **Step 3: Commit** — `test(worker): cover duplicate SQS deliveries end to end`

---

### Task 3: Retry và DLQ *(MVP)*

**Files:** Create `Ai/Worker/WorkerRetryDlqTests.cs`

- [ ] **Step 1: Viết test**

```csharp
R1 Retryable_failures_back_off_then_succeed
// model = new ScriptedModel(Fail(Throttled), Fail(ServiceUnavailable), Valid(5)); DrainAsync(model).
// Assert: model.Calls==3; message duy nhất có VisibilityChanges==[30s,120s] và Deleted;
//   Status Succeeded, Attempt==3, ErrorCode null; Ordinals==[1..5]; Dlq rỗng.

R2 Retryable_failure_on_every_attempt_fails_job_without_dlq
// ScriptedModel(Fail(Throttled), Fail(Throttled), Fail(ServiceUnavailable)); DrainAsync.
// Assert: Status Failed, Attempt==3, ErrorCode=="ServiceUnavailable",
//   ErrorMessage==AiMessages.GenerationFailed(AiErrorCodes.AttemptsExhausted);
//   Ordinals rỗng; note cuối == "AI worker; error=ServiceUnavailable"; message Deleted; Dlq rỗng.

R3 Worker_dying_on_every_receive_sends_message_to_dlq_and_recovery_fails_job
// 3 vòng: msg = Queue.Receive().Single(m => m.JobId==jobId); CrashAfterClaimAsync(jobId);
//   ExpireLeaseAsync(jobId); Time.Advance(300s).
// Vòng 4: Queue.Receive() không chứa jobId; Dlq có đúng 1 message của jobId.
// RunRecoveryAsync() → Status Failed, Attempt==3, ErrorCode=="AttemptsExhausted",
//   note cuối bắt đầu "AI worker recovery; error=AttemptsExhausted"; Ordinals rỗng.
```

- [ ] **Step 2: Chạy → PASS** (FAIL → như Task 2).

- [ ] **Step 3: Commit** — `test(worker): cover retry backoff, attempt exhaustion and DLQ redrive`

---

### Task 4: Crash giữa chừng

**Files:** Create `Ai/Worker/WorkerCrashTests.cs`

- [ ] **Step 1: Viết test**

```csharp
C1 Failure_inside_save_transaction_leaves_nothing_and_retry_completes_once  [PostgresFact(Timeout = 60_000)]
// Mở NpgsqlConnection riêng (fixture.ConnectionString), BEGIN, LOCK TABLE questions IN SHARE MODE
//   → INSERT câu hỏi chờ hết lock_timeout SAU KHI job đã được UPDATE Succeeded trong transaction
//   (sửa sau review: khóa hàng users làm lỗi trước mọi lệnh ghi nên không chứng minh tính nguyên khối).
// msg = Receive(); HandleAsync(msg, new ScriptedModel(Valid(5)))  // CompleteAsync chờ 2s rồi lỗi
// Assert: Status Queued, Attempt==1, ErrorCode=="Unknown"; Ordinals rỗng;
//   không note nào chứa "questions="; msg.VisibilityChanges==[30s].
// ROLLBACK khóa; DrainAsync(new ScriptedModel(Valid(5))).
// Assert: Status Succeeded, Attempt==2, Ordinals==[1..5] (không trùng); UsageTokensAsync==40.

C2 Dead_worker_is_recovered_resent_and_zombie_writes_are_rejected
// msg = Receive(); stale = CrashAfterClaimAsync(jobId); ExpireLeaseAsync(jobId);
// RunRecoveryAsync() → Status Queued.
// AgeLastEnqueuedAsync(jobId, 11 phút); RunRecoveryAsync() → Queue có message mới (ReceiveCount 0) cho jobId.
// DrainAsync(new ScriptedModel(Valid(5))) → xử lý message mới, rồi message cũ (sau Advance) bị xóa vì Finished.
// Zombie: DbContext mới, EfAiJobWorkStore.FailAsync(stale, "Unknown", "x") == false
//   và ReleaseForRetryAsync(stale, …) == false.
// Assert: Status Succeeded, Attempt==2, Ordinals==[1..5], 1 note Succeeded; Dlq rỗng.
```

- [ ] **Step 2: Chạy → PASS** (FAIL → như Task 2).

- [ ] **Step 3: Commit** — `test(worker): cover crash inside save transaction and dead-worker recovery`

---

### Task 5: Vượt quota

**Files:** Create `Ai/Worker/WorkerQuotaTests.cs`

- [ ] **Step 1: Viết test**

```csharp
Q1 User_token_quota_reached_fails_job_without_calling_model
// SetUsageAsync(user, Quota.TokensPerUserPerDay /*100_000*/); model = new ScriptedModel(); DrainAsync.
// Assert: model.Calls==0; Status Failed, ErrorCode=="QuotaExceeded";
//   note cuối == "AI worker; error=QuotaExceeded"; message Deleted; Dlq rỗng; Ordinals rỗng.

Q2 System_token_quota_reached_fails_job_without_calling_model
// Quota.SystemTokensPerDay = 1_000; user khác (CreateSubjectUserAsync) SetUsageAsync(other, 1_000).
// Cùng các khẳng định như Q1.

Q3 Quota_reached_after_generation_skips_verification_but_keeps_questions
// Generation.VerifyAnswers = true; SetUsageAsync(user, 100_000 - 20);
// model = new ScriptedModel(Valid(5)); DrainAsync.
// Assert: model.Calls==1 (không có lần gọi kiểm chứng); Status Succeeded; Ordinals==[1..5];
//   Verifications toàn AiVerificationValues.NotChecked; UsageTokensAsync==100_000.
```

- [ ] **Step 2: Chạy → PASS** (FAIL → như Task 2).

- [ ] **Step 3: Chạy toàn bộ backend để chắc không vỡ test khác**

Run: `dotnet build ExamMaker.slnx -c Release && dotnet test ExamMaker.slnx -c Release --no-build`
Expected: 0 failed; test `Ai.Worker` không bị Skip khi có `A2_TEST_PG_ADMIN`.

- [ ] **Step 4: Đánh dấu B7 trong `docs/tasks.md`** chỉ khi PR đã merge và CI xanh (định nghĩa "xong"); trong PR chỉ ghi "Closes #<issue B7>".

- [ ] **Step 5: Commit** — `test(worker): cover token quota limits end to end`
