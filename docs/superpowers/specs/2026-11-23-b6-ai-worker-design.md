# B6 — Worker xử lý job sinh câu hỏi AI: thiết kế

> Ngày 23/11/2026 · Task B6 trong `docs/tasks.md` · Ngữ cảnh: `docs/context.md` mục 4.1.1, 4.1.2 (B3, B4), 8.1.
> Phụ thuộc đã xong: A2 (schema), O11 (SQS + DLQ), B3/B4 (lõi AI), phía API của B5 (tạo job + gửi SQS).

## 1. Mục đích

API (B5) đã tạo job `Queued`, giữ chỗ quota lượt sinh và gửi `{jobId}` lên SQS, nhưng chưa có gì tiêu thụ hàng đợi: `ExamMaker.Worker` mới chỉ có khung Serilog + Parameter Store. B6 viết phần nhận job, gọi Bedrock qua lõi B3/B4 và lưu câu `Draft`, đúng các quy tắc chống xử lý trùng và phục hồi ở mục 4.1.1, để luồng demo chính (mục 2.1) chạy được đầu–cuối.

## 2. Mục tiêu / Không làm / Phản mục tiêu

**Mục tiêu**

- Mỗi job được xử lý thành công **tối đa một lần** dù SQS giao trùng, worker crash hay deploy giữa chừng.
- Mọi token đã tiêu đều được ghi vào job và `ai_usage_daily`, kể cả khi job sau đó lỗi.
- Không gọi Bedrock khi người yêu cầu hoặc toàn hệ thống đã chạm trần token ngày.
- Job không bao giờ kẹt vĩnh viễn ở `Queued`/`Running` mà không có cảnh báo.
- Logic xử lý test được bằng Bedrock giả + PostgreSQL thật mà không cần SQS (phục vụ B7).

**Không làm trong B6**

- Bộ test lỗi đầy đủ (giao trùng đầu–cuối, crash giữa transaction, DLQ, vượt quota) — thuộc **B7**. B6 chỉ có test đơn vị và test tích hợp cho từng mảnh.
- Frontend polling/rà soát (B8), `/admin/ai-usage` (B8), alarm CloudWatch (O-task giám sát).
- Đổi prompt, parser, bộ lọc trùng hay bộ kiểm chứng của B3/B4.

**Phản mục tiêu** (chạy được nhưng vẫn là thất bại)

- Lưu câu hai lần cho cùng job, hoặc xóa message SQS trước khi commit.
- Một worker đã mất lease vẫn ghi đè kết quả của worker khác.
- Tiêu token sau khi đã biết vượt quota; hoặc làm mất token đã tiêu khỏi sổ quota.
- Đưa nội dung đầu ra model hay thông điệp lỗi gốc của AWS vào `error_message`/log.

## 3. Ràng buộc

- Chạy cùng máy với API trên `t4g.small` (2 GB RAM); quy mô ≤ 20 người dùng đồng thời.
- SQS standard, visibility timeout **300 s**, `maxReceiveCount = 3` → DLQ; `AiQuota:MaxAttempts = 3` khớp con số này.
- Quota (mục 14): 100.000 token/user/ngày, 500.000 token/ngày toàn hệ thống, ngày theo UTC.
- Không dùng access key; EC2 dùng instance profile. Cấu hình qua Parameter Store.
- Đổi schema chỉ qua EF Core migration, kiểu tương thích ngược; sửa `docs/context.md` trước khi code.
- Không thêm dịch vụ AWS ngoài mục 5.2.

## 4. Các hướng đã cân nhắc

| Hướng | Nội dung | Đánh đổi |
| --- | --- | --- |
| **A. Lõi xử lý ở Application, Worker chỉ lo vòng lặp** *(đã chọn)* | `AiJobProcessor` ở `Application/Ai` chạy một lần xử lý qua interface; EF/SQS/Bedrock/heartbeat ở Infrastructure; Worker có 2 `BackgroundService`. | Nhiều interface/file hơn, nhưng cùng kiểu B3/B4 và tạo đúng điểm cắt cho B7. |
| B. Gom vào một `BackgroundService` gọi EF/SQS trực tiếp | Ít file. | Lệch cách chia tầng; B7 phải dựng cả host, khó giả lập crash giữa transaction/xóa message. |
| C. Thư viện xử lý message (AWS Message Processing Framework, MassTransit…) | Có sẵn vòng nhận, gia hạn visibility. | Thêm phụ thuộc lớn; thư viện tự quyết thời điểm xóa và backoff — đúng hai điểm mục 4.1.1 đòi tự kiểm soát. |

Người dùng chọn **A** (23/11/2026).

## 5. Quyết định của người dùng

- **Thêm cột `ai_generation_jobs.last_enqueued_at`** bằng migration để tác vụ gửi lại job kẹt `Queued` biết lần gửi SQS gần nhất, tránh gửi lại mỗi lượt quét (23/11/2026).
- **Hướng A** ở mục 4.

**Do người viết thiết kế đề xuất (chưa được xác nhận riêng, cần xem khi duyệt):**

- Vượt trần token trước lần kiểm chứng → bỏ kiểm chứng (`NotChecked`) và vẫn lưu câu; vượt trước lần sinh → `Failed` `QuotaExceeded` (6.5).
- Lượt sinh không hoàn lại khi job lỗi (6.5).
- Người yêu cầu bị khóa/đổi tổ trước khi job chạy xong → `Failed` `RequesterUnavailable`, không lưu câu (6.3, 6.6).
- Audit câu do AI tạo dùng actor NULL (hệ thống) kèm note, không dùng actor là giáo viên (6.6).
- Lần thử cuối gặp lỗi retryable → worker tự đặt `Failed` và xóa message, DLQ chỉ nhận message của worker chết (6.8).
- Tắt worker khi deploy không tính là một lần thử (6.8).
- Không tự `Failed` job `Queued` lâu ngày, chỉ cảnh báo (6.8).
- Chạy song song 2 job (6.10).

## 6. Thiết kế

### 6.1 Thay đổi schema

- Cột `last_enqueued_at timestamptz NOT NULL`. Migration thêm cột nullable, backfill `= created_at`, rồi đặt `NOT NULL`.
- `EfAiJobStore.TryCreateQueuedJobAsync` đặt `LastEnqueuedAt = CreatedAt`. API **không** cập nhật lại sau khi gửi SQS (gửi lỗi thì tác vụ định kỳ lo).
- Không thêm index: lượt quét lọc `status = 'Queued'` trên vài nghìn dòng là đủ ở quy mô MVP; index `(status, lease_expires_at)` sẵn có phục vụ quét lease.
- Thêm mã lỗi `QuotaExceeded`, `RequesterUnavailable`, `AttemptsExhausted` vào `AiErrorCodes` và câu tiếng Việt tương ứng trong `AiMessages.GenerationFailed` (không cần migration: `error_code` là text).

### 6.2 Thành phần

**Application (`Application/Ai/Worker/`)** — thuần logic, không gọi AWS:

- `AiJobProcessor.ProcessAsync(Guid jobId, IAiJobLease lease, CancellationToken ct) → AiJobOutcome` — một lần xử lý một message.
- `AiJobOutcome`: `Delete` (xóa message), `RetryAfter(TimeSpan)` (đặt visibility rồi để message quay lại), `Abandon` (không đụng message — mất lease hoặc đang tắt).
- `IAiJobWorkStore` — mọi đọc/ghi DB của worker (mục 6.4–6.6), mỗi lệnh ghi sau claim có **fencing** theo `attempt` đã claim.
- `IAiJobLease` — heartbeat: gia hạn lease DB + visibility SQS; báo khi mất lease.
- `QuotaGatedModelClient : IAiModelClient` — decorator kiểm trần token **trước mỗi lần gọi** model (mục 6.5).
- `AiJobRecovery` — logic tác vụ định kỳ (mục 6.8), dùng `IAiJobWorkStore` + `IAiJobQueue`.
- `AiWorkerOptions` (mục 6.10).

**Infrastructure:**

- `BedrockAiModelClient : IAiModelClient` — Converse API (`AWSSDK.BedrockRuntime`), mục 6.9.
- `EfAiJobWorkStore : IAiJobWorkStore` — SQL có điều kiện + transaction.
- `SqsAiJobLease : IAiJobLease` — `ChangeMessageVisibility` + gia hạn `lease_expires_at`.
- `AddExamMakerAiWorker(...)` — DI riêng cho Worker (không đăng ký vào API).

**Worker:**

- `AiJobConsumerService : BackgroundService` — long-poll SQS, chạy tối đa `MaxConcurrency` job song song, áp `AiJobOutcome` lên message.
- `AiJobRecoveryService : BackgroundService` — chạy `AiJobRecovery` mỗi `RecoveryIntervalSeconds`.
- Log heartbeat định kỳ của worker (mục 10 context: "không có heartbeat → cảnh báo").

### 6.3 Luồng xử lý một message

1. Nhận message, parse `{jobId}`. Body hỏng → log cảnh báo, **xóa** message (không có job nào để cứu).
2. **Claim** (mục 6.4). Không claim được → xử lý theo bảng 6.4, kết thúc.
3. Bắt đầu heartbeat (mục 6.7) và hạn tổng `JobTimeoutMinutes`.
4. **Nạp ngữ cảnh**: parse `params` → `AiGenerateRequest`; đọc tên môn/chương, tổ của môn; đọc user yêu cầu.
   - `params` hỏng, môn/chương không còn → `Failed` `InvalidRequest`.
   - User không active, không phải teacher/head, hoặc `department_id` ≠ tổ của môn → `Failed` `RequesterUnavailable` (không tốn token).
5. **Sinh câu** (B3) qua `QuotaGatedModelClient`, callback `onModelCall` = ghi token (mục 6.5).
6. **Lọc trùng** (B4) với khóa từ `IAiDuplicateKeySource`.
7. **Kiểm chứng** (B4, nếu bật) trên các câu còn lại, cùng client + callback.
8. **Lưu kết quả** trong một transaction (mục 6.6), commit.
9. Trả `Delete` → consumer xóa message **sau** commit.

Lỗi ở bất kỳ bước nào → mục 6.8 "Phân loại lỗi".

### 6.4 Claim và các trường hợp không claim được

```sql
UPDATE ai_generation_jobs
SET status = 'Running', attempt = attempt + 1,
    lease_expires_at = now() + @lease, started_at = COALESCE(started_at, now()),
    prompt_version = @promptVersion  -- AiPromptBuilder.Version, để job lỗi vẫn có phiên bản prompt
WHERE id = @id AND attempt < max_attempts
  AND (status = 'Queued' OR (status = 'Running' AND lease_expires_at < now()))
RETURNING attempt;
```

`attempt` trả về là **fencing token**: mọi lệnh ghi sau đó (`heartbeat`, `release`, `fail`, `complete`) có thêm `AND status = 'Running' AND attempt = @attempt`; 0 dòng nghĩa là đã mất lease → dừng, trả `Abandon`.

Claim trả 0 dòng → đọc lại job:

| Trạng thái job | Hành động |
| --- | --- |
| Không tồn tại | Log cảnh báo, `Delete` |
| `Succeeded` / `Failed` | `Delete` |
| `Running`, lease còn hạn | Worker khác đang giữ → `RetryAfter(min(300 s, thời gian lease còn lại + 5 s))`. Không xóa: nếu worker kia vừa chết thì chính message này cứu job ngay khi lease hết, không phải chờ tác vụ định kỳ. Job tối đa 10 phút nên message trùng không đủ 3 lần nhận để vào DLQ |
| `Queued`/`Running` hết lease nhưng `attempt >= max_attempts` | Đặt `Failed` (`error_code` giữ mã lỗi gần nhất, không có thì `AttemptsExhausted`), ghi audit, `Delete` |

### 6.5 Token và quota

- **Trước mỗi lần gọi model** (sinh, sửa lại, kiểm chứng), `QuotaGatedModelClient` đọc token đã dùng hôm nay (UTC) của user và tổng hệ thống; nếu `≥ TokensPerUserPerDay` hoặc `≥ SystemTokensPerDay` → ném `AiQuotaExceededException`, **không gọi** Bedrock.
- **Sau mỗi lần gọi** (callback `onModelCall`): một transaction ngắn, commit ngay, không fencing (token đã tiêu là đã tiêu):
  - `UPDATE ai_generation_jobs SET input_tokens += …, output_tokens += …, model_id = …` (`prompt_version` đã đặt lúc claim; lần gọi kiểm chứng không ghi đè nó)
  - `INSERT INTO ai_usage_daily (user_id, date, jobs, tokens) VALUES (…, 0, …) ON CONFLICT DO UPDATE SET tokens = tokens + …` (ngày = ngày UTC lúc gọi; `jobs` không đổi vì API đã giữ chỗ).
- Hệ quả:
  - Vượt trần **trước lần sinh đầu tiên hoặc lần sửa lại** → `Failed` `QuotaExceeded` (không thử lại).
  - Vượt trần **trước lần kiểm chứng** → `AiAnswerVerifier` nuốt lỗi như mọi lỗi model khác → các câu `NotChecked`, job vẫn lưu câu và `Succeeded`. (Đáp ứng lưu ý B4 (1).)
- Lần gọi có thể vượt trần một chút (kiểm trước khi biết số token của chính lần đó) — chấp nhận, giống API.
- Lượt sinh (`ai_usage_daily.jobs`) **không hoàn lại** khi job lỗi.

### 6.6 Transaction lưu kết quả

Trong **một** transaction, `lock_timeout` 2 s:

1. `SELECT … FROM users WHERE id = @requester FOR SHARE`; kiểm lại active/role/tổ như bước 4. Không đạt → rollback, chuyển sang `Failed` `RequesterUnavailable` (transaction riêng).
2. `UPDATE ai_generation_jobs SET status='Succeeded', finished_at=now(), lease_expires_at=NULL, error_code=NULL, error_message=NULL, duplicates_dropped=@n WHERE id=@id AND status='Running' AND attempt=@attempt`. 0 dòng → rollback, `Abandon`.
3. Chèn từng câu: `status='Draft'`, `source='AI'`, `revision=1`, `ai_job_id`, `ai_ordinal` (ordinal gốc, có khoảng trống), `ai_verification`/`ai_verification_note` từ B4 (mặc định `NotChecked`), `ai_reviewed_by_author=false`, `department_id` = tổ của môn, `created_by` = requester, `created_at = updated_at`.
4. Audit (qua `AuditLog.Create`, `department_id` = tổ của môn, actor NULL vì là hệ thống):
   - mỗi câu: `Question` / `CreateQuestion`, note `AI worker; job=<id>; ordinal=<n>; revision=1`;
   - một dòng `AiJob` / `Succeeded` (`Running → Succeeded`), note `AI worker; questions=<k>; duplicatesDropped=<n>`.
5. Commit → `Delete`.

Vi phạm unique `(ai_job_id, ai_ordinal)` không xảy ra khi có fencing; nếu vẫn gặp → rollback, đọc lại job, xử lý như bảng 6.4.

Không lấy advisory lock danh mục 48683402: job còn `Running` cho tới commit nên A5 đã chặn xóa/chuyển môn/chương (`EnsureNoActiveJobsAsync`).

Job `Failed` cũng ghi một dòng audit `AiJob` (`Running → Failed`, note chứa `error_code`).

### 6.7 Heartbeat và ngân sách thời gian

- Lease DB = visibility = **300 s**. Heartbeat mỗi **60 s**: trước tiên `UPDATE … SET lease_expires_at = now() + 300s WHERE … fencing`, **sau đó** `ChangeMessageVisibility(300)` — thứ tự này để visibility luôn hết sau lease, nên khi worker chết, message quay lại sẽ gặp lease đã hết và claim được ngay.
- Heartbeat gặp 0 dòng (mất lease) → hủy xử lý, `Abandon`. `ChangeMessageVisibility` lỗi → log, thử lại ở nhịp sau (lease DB vẫn bảo vệ tính đúng).
- Mỗi lần gọi Bedrock tối đa `Bedrock:TimeoutSeconds` (120 s); tổng một job tối đa `JobTimeoutMinutes` (10 phút) → quá hạn coi là lỗi `Timeout` (retryable). Trường hợp xấu nhất: sinh + sửa lại + kiểm chứng ≈ 6 phút, nằm trong hạn và được heartbeat che. (Đáp ứng lưu ý B4 (2).)

### 6.8 Phân loại lỗi và tác vụ định kỳ

**Phân loại lỗi** (sau khi đã claim):

| Lỗi | Xử lý job | Message |
| --- | --- | --- |
| `AiQuotaExceededException` | `Failed` `QuotaExceeded` | `Delete` |
| Lỗi nạp ngữ cảnh / người yêu cầu (bước 4, 6.6.1) | `Failed` `InvalidRequest` / `RequesterUnavailable` | `Delete` |
| Non-retryable theo `AiErrorClassifier` | `Failed` + mã | `Delete` |
| Retryable, `attempt < max_attempts` | `Queued`, `lease_expires_at=NULL`, `last_enqueued_at=now()` (để tác vụ định kỳ không gửi lại trong lúc chờ backoff), lưu `error_code`/`error_message` (câu "hệ thống sẽ thử lại") | `RetryAfter(AiRetryPolicy.VisibilityDelay(attempt))` |
| Retryable, `attempt = max_attempts` | `Failed` + mã | `Delete` (DLQ chỉ còn nhận message của worker chết giữa chừng → alarm DLQ có ý nghĩa) |
| Lỗi DB khi đọc khóa trùng | coi là retryable (`Unknown`) theo classifier | như trên (đáp ứng lưu ý B4 (3)) |
| Host đang tắt (`OperationCanceledException` do stop) | `Queued`, `attempt = attempt - 1` (không phạt deploy), `last_enqueued_at=now()` | `ChangeMessageVisibility(0)` rồi `Abandon` |
| Mất lease | không ghi gì | `Abandon` |

Mọi lệnh ghi ở bảng trên đều có fencing; 0 dòng → `Abandon`.

**Tác vụ định kỳ** (`AiJobRecoveryService`, mỗi 60 s):

1. **Job treo**: `Running` có `lease_expires_at < now()`:
   - còn lượt → `Queued`, `lease_expires_at = NULL`, `last_enqueued_at = now()` (message gốc sẽ tự quay lại sau visibility);
   - hết lượt → `Failed` (`error_code` gần nhất hoặc `AttemptsExhausted`) + audit.
2. **Job kẹt `Queued`**: `last_enqueued_at < now() - RequeueAfterMinutes` (10 phút, lớn hơn backoff tối đa 5 phút) → `UPDATE … SET last_enqueued_at = now() WHERE id = @id AND status = 'Queued' AND last_enqueued_at = @old`; được 1 dòng thì gửi SQS `{jobId}` (an toàn vì worker idempotent). Gửi lỗi → log, lượt sau thử lại.
3. **Cảnh báo**: còn job `Queued` có `created_at < now() - StaleQueuedWarnMinutes` (15 phút) → log `Warning` có số lượng (để metric filter/alarm bắt). Không tự `Failed` job cũ vì EC2 tắt ban đêm, job sẽ chạy sáng hôm sau.

### 6.9 Adapter Bedrock

- `AWSSDK.BedrockRuntime`, Converse API; `ModelId` từ `Bedrock:ModelId` (Parameter Store, có thể là ID inference profile). Region theo `AWS:Region`.
- SDK **không tự retry** (`MaxErrorRetry = 0`), retry do SQS đảm nhận; timeout mỗi lần gọi `Bedrock:TimeoutSeconds`.
- Ánh xạ: `ThrottlingException` → `Throttled`; `ModelTimeoutException`/hết thời gian → `Timeout`; `ServiceUnavailableException`, `InternalServerException`, `ModelNotReadyException` → `ServiceUnavailable`; `AccessDeniedException` → `AccessDenied`; `ValidationException`, `ResourceNotFoundException` → `InvalidRequest`; `HttpRequestException`/`IOException` → `Network`.
- `stopReason`: `end_turn`/`stop_sequence` → `EndTurn`; `max_tokens` → `MaxTokens`; `content_filtered`/`guardrail_intervened` → `ContentFiltered`; khác → `Other`.
- Log chỉ ghi loại lỗi/mã AWS, không ghi message gốc hay nội dung prompt/đầu ra.

### 6.10 Cấu hình

| Khóa | Mặc định | Ghi chú |
| --- | --- | --- |
| `Sqs:AiJobsQueueUrl` | — | Đã có. Thiếu ở Development → consumer + recovery tắt, log cảnh báo; môi trường khác → startup lỗi. |
| `Bedrock:ModelId` | — | Thiếu: xử lý như trên. |
| `Bedrock:TimeoutSeconds` | 120 | |
| `Worker:MaxConcurrency` | 2 | |
| `Worker:LeaseSeconds` | 300 | Phải ≤ visibility timeout của queue. |
| `Worker:HeartbeatSeconds` | 60 | |
| `Worker:JobTimeoutMinutes` | 10 | |
| `Worker:RecoveryIntervalSeconds` | 60 | |
| `Worker:RequeueAfterMinutes` | 10 | Phải > backoff tối đa 300 s. |
| `Worker:StaleQueuedWarnMinutes` | 15 | |
| `AiGeneration:*`, `AiQuota:*` | | Đã có (B3, B4, B5). |

IAM instance profile cần thêm: `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:ChangeMessageVisibility` trên queue chính; `bedrock:InvokeModel` trên model/inference profile đang dùng. Ghi vào runbook, không commit ARN.

### 6.11 Kiểm thử trong B6

- **Application.Tests** (fake store/lease/model): mọi hàng ở bảng 6.4 và 6.8; quota chặn trước lần sinh và trước lần kiểm chứng; mất lease giữa chừng; `duplicates_dropped` và `ai_verification` truyền đúng sang lệnh lưu; `AiJobRecovery`.
- **Infrastructure.Tests** (PostgreSQL thật): claim SQL (Queued, Running hết lease, hết lượt, đã xong), fencing ở mọi lệnh ghi, transaction lưu kết quả + audit, upsert token, các truy vấn quét, migration `last_enqueued_at`.
- Hàm ánh xạ lỗi/stop reason của Bedrock tách ra để test thuần.
- Không gọi Bedrock/SQS thật trong CI.

### 6.12 Tài liệu cần cập nhật (trước khi code)

- `docs/context.md`: 4.1.1 (fencing, xử lý message trùng, shutdown, `last_enqueued_at`), thêm "Chi tiết triển khai B6", 8.1 (cột mới), lịch sử thay đổi 0.20.
- `docs/erd.md`: cột `last_enqueued_at`.
- `docs/tasks.md`: đánh dấu B6 khi xong.
- Runbook hạ tầng: quyền IAM mục 6.10, chạy service Worker.

## 7. Để người code tự quyết

- Tên lớp/file cụ thể, cách chia method trong `EfAiJobWorkStore`.
- Dùng EF hay SQL thô cho từng lệnh (claim/fencing nên là SQL thô có `RETURNING`).
- Cách hiện thực song song trong consumer (`Channel`, `SemaphoreSlim`…), miễn tôn trọng `MaxConcurrency` và chỉ nhận thêm message khi còn chỗ.
- Hình thức log heartbeat của worker.
