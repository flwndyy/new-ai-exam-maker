# B7 — Test lỗi đầu–cuối cho Worker AI (thiết kế ngắn)

> Task B7 trong `docs/tasks.md`, phụ thuộc B6 (#31, đã merge vào `dev`). Ngữ cảnh: `docs/context.md` mục 4.1.1, "Chi tiết triển khai B6"; spec B6 `2026-11-23-b6-ai-worker-design.md` (dòng 18, 22 giao phần này cho B7).

## Mục đích

B6 đã có test đơn vị (store/model giả) và test PostgreSQL cho **từng mảnh** (claim, fencing, lưu kết quả, quét). Chưa có test nào chạy **cả chuỗi thật** — `AiJobMessageHandler` → `AiJobProcessor` → `EfAiJobWorkStore` / `AiJobRecovery` trên PostgreSQL — để chứng minh các bảo đảm ở mục 4.1.1 giữ được khi các mảnh ghép với nhau. B7 lấp khoảng trống đó; xong B7 thì đóng issue khi PR merge và CI xanh.

## Mục tiêu / không làm

- **Làm**: bốn nhóm kịch bản dưới đây, chạy trong `ExamMaker.Infrastructure.Tests` bằng `[PostgresFact]` (bắt buộc trong CI qua service PostgreSQL), Bedrock giả, hàng đợi giả trong bộ nhớ.
- **Không làm**: không sửa code worker (nếu test lộ bug thì báo và sửa trong cùng PR, ghi rõ); không gọi Bedrock/SQS thật; không dựng host `BackgroundService` (vòng lặp nhận message đã có `AiJobConsumerServiceTests`).
- **Phản mục tiêu**: test chậm (chờ thời gian thật 300s) hoặc chập chờn do chạy chung DB với test khác.

## Cách tiếp cận (đã chọn: A)

| Phương án | Đánh đổi |
| --- | --- |
| **A. Hàng đợi giả trong bộ nhớ mô phỏng SQS** *(đã chọn)* | Nhanh, không thêm phụ thuộc; kiểm được logic của mình, không kiểm cấu hình redrive SQS thật (cấu hình đó thuộc O11, đã kiểm tay). |
| B. LocalStack trong CI | Gần SQS thật hơn nhưng thêm container, CI chậm và dễ lỗi vặt. |
| C. Chỉ kiểm phía DB (hết lượt → `Failed`) | Đơn giản nhất nhưng không chứng minh hành vi message. |

## Thiết kế

**`FakeSqsQueue`** (file test mới, cài `IAiJobQueue`): giữ danh sách message, mỗi message có `ReceiveCount`, `VisibleAt`; đồng hồ là `FakeTimeProvider`.
- `ReceiveAsync()` trả các message đã hiện, tăng `ReceiveCount`, đặt `VisibleAt = now + 300s` (visibility mặc định của hàng đợi). Message sắp được nhận lần thứ 4 (`maxReceiveCount = 3`) chuyển sang danh sách `Dlq` thay vì trả về.
- Message trả về cài `IAiQueueMessage`: `DeleteAsync` xóa khỏi hàng đợi, `ChangeVisibilityAsync(t)` đặt `VisibleAt = now + t`. Ghi lại lịch sử thao tác để test kiểm (vd backoff 30s → 120s).
- `EnqueueAsync(jobId)` thêm message `{"jobId":"…"}` mới với `ReceiveCount = 0` (dùng cho requeue khi dừng host và cho `AiJobRecovery` gửi lại).

**Harness** `WorkerHarness`: dựng processor/handler/recovery thật trên `fixture.CreateContext()` (mỗi lần xử lý một DbContext riêng, như một worker), model giả có kịch bản (trả JSON hợp lệ / ném `AiModelException` retryable / chặn chờ tín hiệu để mô phỏng chạy song song).

**Lưu ý đồng hồ**: lease dùng `now()` của PostgreSQL, không theo `FakeTimeProvider`. Để mô phỏng "lease hết hạn sau crash", test đặt `lease_expires_at` về quá khứ bằng SQL (`AiTestData.ExecuteAsync`) thay vì chờ. Quota đọc theo ngày UTC từ `TimeProvider`, nên test quota dùng `AiTestData.UniqueDate()` để không đụng dữ liệu test khác (trần hệ thống cộng mọi user trong ngày).

## Kịch bản

1. **Giao trùng message** *(MVP bắt buộc)*
   - Message giao lại sau khi job đã `Succeeded` → xóa message, model không được gọi thêm, không thêm câu/audit/token.
   - Hai worker xử lý song song cùng `jobId` (model của worker 1 bị chặn) → đúng một worker claim; worker 2 hẹn message quay lại theo lease còn lại; kết thúc đúng **một** bộ câu, một audit `Succeeded`, token chỉ của các lần gọi thật.
2. **Retry / DLQ** *(MVP bắt buộc)*
   - Lỗi retryable ở lần 1, 2 → job về `Queued`, visibility 30s rồi 120s; lần 3 thành công → `Succeeded`, message bị xóa.
   - Lỗi retryable cả 3 lần → lần 3 worker tự `Failed` (`AttemptsExhausted` message, giữ mã lỗi gần nhất) và xóa message; `Dlq` rỗng.
   - Worker "chết" sau khi claim (không áp outcome) ở cả 3 lần nhận → lần nhận thứ 4 message vào `Dlq`; `AiJobRecovery` chạy sau khi lease hết hạn đưa job về `Failed` có audit recovery, không treo `Running`.
3. **Crash giữa chừng**
   - Lỗi trong transaction lưu kết quả (vd user bị khóa giữa chừng, hoặc ném lỗi ở lần lưu đầu) → không còn câu nào được lưu, job không `Succeeded`; lần giao lại sau đó hoàn tất đúng một lần với `(ai_job_id, ai_ordinal)` không trùng.
   - Worker chết sau claim, lease hết hạn → `AiJobRecovery` đưa về `Queued` + gửi lại message → worker khác hoàn tất; worker cũ quay lại ghi (fenced) bị từ chối, không ghi đè.
4. **Vượt quota**
   - Token user hoặc hệ thống đã chạm trần trước lần sinh → `Failed` `QuotaExceeded`, model giả **không** được gọi, message bị xóa.
   - Chạm trần sau lần sinh, trước bước kiểm chứng (bật `VerifyAnswers`) → câu vẫn lưu với `NotChecked`, job `Succeeded`.

Mỗi kịch bản khẳng định trên **DB thật** (trạng thái job, `attempt`, số câu, `audit_logs`, `ai_usage_daily`) và trên **hàng đợi giả** (đã xóa / visibility / DLQ).

## Để lại cho người làm

Tên file/test cụ thể, cách chia harness, chọn câu lệnh SQL để mô phỏng lỗi lưu — tùy người làm, miễn bám kịch bản trên. Thứ tự: nhóm 1 và 2 trước (phạm vi MVP), rồi 3, 4.
