# Runbook — Triển khai & Vận hành Amazon SQS + DLQ (Task O11)

Tài liệu hướng dẫn dựng, kiểm tra, bàn giao và xử lý sự cố cho **Amazon SQS + Dead-Letter Queue (DLQ)** phục vụ pipeline sinh câu hỏi AI của dự án **AI Exam Maker** theo đúng kiến trúc tại `docs/context.md` (mục 4.1.1, 5.2, 6, 10).

> **Quy tắc bảo mật:** Tuyệt đối **không** commit Account ID, Queue URL/ARN thật vào repository. Toàn bộ Queue URL/ARN được lưu trong **AWS Systems Manager Parameter Store** dưới `/exammaker/<env>/Sqs/*` (Standard tier, chi phí **$0**).

---

## 1. Thông số kiến trúc chuẩn & Kiểm soát chi phí

| Thành phần | Giá trị chuẩn | Tham chiếu |
|---|---|---|
| Region | `ap-southeast-1` (Singapore) | `docs/context.md` §14 |
| Loại hàng đợi | **Standard Queue** (không dùng FIFO vì Worker đã thiết kế idempotent bằng DB `UPDATE` có điều kiện theo `jobId` và dùng `ChangeMessageVisibility` backoff) | `docs/context.md` §4.1.1 |
| Main Queue | `exammaker-dev-ai-jobs` | `infra/sqs/setup-sqs.sh` |
| Dead-Letter Queue (DLQ) | `exammaker-dev-ai-jobs-dlq` | `infra/sqs/setup-sqs.sh` |
| `RedrivePolicy` | `maxReceiveCount = 3` → chuyển sang `exammaker-dev-ai-jobs-dlq` (khớp `max_attempts = 3` trong bảng `ai_generation_jobs`) | `docs/context.md` §4.1.1, §8.1 |
| `VisibilityTimeout` | **`300` giây (5 phút)** (> thời gian xử lý tối đa 1 lượt gọi Bedrock + validate; Worker chủ động gia hạn bằng `ChangeMessageVisibility` khi cần) | `docs/context.md` §4.1.1 |
| Long Polling | **`ReceiveMessageWaitTimeSeconds = 20` giây** — giảm tối đa empty receive requests để giữ tổng request (~260k/tháng) nằm gọn trong **1.000.000 requests/tháng Always Free** | `docs/context.md` §6 |
| `MessageRetentionPeriod` | Main Queue: **4 ngày** (`345600`s) · DLQ: **14 ngày** (`1209600`s) để đủ thời gian điều tra job lỗi | `docs/context.md` §4.1.1 |
| Mã hóa lưu trữ (at rest) | **SSE-SQS (`SqsManagedSseEnabled = true`)** — miễn phí **$0** (không dùng KMS CMK tốn $1/tháng) | `docs/context.md` §5.2, §6 |
| Dự toán chi phí | **$0.00/tháng** (AWS SQS Always Free ≤ 1.000.000 requests/tháng) | `docs/context.md` §6 |

---

## 2. Cách 1: Dựng tự động bằng AWS CLI script (Khuyên dùng)

**Yêu cầu công cụ:** `aws` (CLI v2), `jq` — cả 2 đều cài sẵn trong **AWS CloudShell** (`ap-southeast-1`). Script tích hợp sẵn `MSYS_NO_PATHCONV=1` cho Git Bash trên Windows.

```bash
./infra/sqs/setup-sqs.sh
```

**Hành vi idempotent:**
- Tạo (nếu chưa có) và đồng bộ lại cấu hình (`set-queue-attributes`, `tag-queue`) cho cả DLQ `exammaker-dev-ai-jobs-dlq` và Main Queue `exammaker-dev-ai-jobs` — bao gồm `RedrivePolicy` (`maxReceiveCount=3` cố định khớp `AiQuota:MaxAttempts=3`) và `RedriveAllowPolicy` trên DLQ (chỉ cho phép Main Queue trỏ tới).
- Chỉ coi lỗi `NonExistentQueue` / `QueueDoesNotExist` là chưa có queue; mọi lỗi khác (`AccessDenied`, `Throttling`) sẽ dừng script ngay và in lỗi thật.
- Cập nhật lại 4 tham số metadata lên SSM Parameter Store. Sau đó chạy `./infra/sqs/verify-sqs.sh` để xác minh toàn bộ 12 tiêu chí.

---

## 3. Cách 2: Dựng thủ công trên AWS Console

1. **Tạo Dead-Letter Queue** (*SQS → Create queue*):
   - Type: **Standard** · Name: `exammaker-dev-ai-jobs-dlq`
   - Message retention period: **`14` Days** · Receive message wait time: **`20` seconds**
   - Encryption: **Enabled** → **Amazon SQS key (SSE-SQS)**
   - Tags: `Project=ai-exam-maker`, `Environment=dev`, `Task=O11`, `Role=dlq`
2. **Tạo Main Queue** (*SQS → Create queue*):
   - Type: **Standard** · Name: `exammaker-dev-ai-jobs`
   - Visibility timeout: **`300` seconds** · Message retention period: **`4` Days** · Receive message wait time: **`20` seconds**
   - Encryption: **Enabled** → **Amazon SQS key (SSE-SQS)**
   - Dead-letter queue: **Enabled** → chọn ARN của `exammaker-dev-ai-jobs-dlq` · Maximum receives: **`3`**
   - Tags: `Project=ai-exam-maker`, `Environment=dev`, `Task=O11`, `Role=main`
3. **Lưu 4 tham số vào SSM Parameter Store** (*Systems Manager → Parameter Store*, kiểu `String`, Standard tier):
   - `/exammaker/dev/Sqs/AiJobsQueueUrl` = `<URL Main Queue>`
   - `/exammaker/dev/Sqs/AiJobsQueueArn` = `<ARN Main Queue>`
   - `/exammaker/dev/Sqs/AiJobsDlqUrl` = `<URL DLQ>`
   - `/exammaker/dev/Sqs/AiJobsDlqArn` = `<ARN DLQ>`

---

## 4. Quy ước bàn giao cho DEV-B (`B5`/`B6`) & OPS-1 (`O3`)

- **Backend (.NET 10):** `backend/src/ExamMaker.Infrastructure/Ai/AiServiceCollectionExtensions.cs` đọc cấu hình tại khóa **`Sqs:AiJobsQueueUrl`**. Khi đặt `ParameterStore__Path=/exammaker/dev`, tham số SSM `/exammaker/dev/Sqs/AiJobsQueueUrl` được tự động ánh xạ vào `Sqs:AiJobsQueueUrl`.
- **Ràng buộc cho Worker (`B6`) theo `docs/context.md` §4.1.1:**
  - **`VisibilityTimeout = 300s` (5 phút):** Tổng thời gian xử lý tối đa của 1 lần nhận message (timeout gọi Bedrock + số lần thử sửa JSON + bước kiểm chứng đáp án khi bật ở demo) phải **< 300 giây (có biên an toàn)**, hoặc Worker bắt buộc gia hạn lease định kỳ bằng `ChangeMessageVisibility` trước khi hết hạn.
  - **`maxReceiveCount = 3` (khóa cứng):** Khớp chính xác `AiQuota:MaxAttempts = 3` trong DB.
  - **Poison message (không parse được `jobId`):** Worker nên coi là non-retryable — ghi log cảnh báo và **xóa message ngay**, tránh để rơi vào vòng lặp 3 lần rồi đẩy vào DLQ làm nổ alarm O13.
- **Worker B6 đã triển khai** (chi tiết: `docs/context.md` mục 4.1.2 "Chi tiết triển khai B6"):
  - Tham số Parameter Store cần có cho `exammaker-worker`: `/exammaker/dev/Sqs/AiJobsQueueUrl`, `/exammaker/dev/Bedrock/ModelId` (inference profile ID, không dùng ARN vì `model_id` được trả về cho giáo viên; xem `runbooks/parameter-store.md`), `/exammaker/dev/Bedrock/Region`, `/exammaker/dev/ConnectionStrings/ExamMaker`. Tùy chọn: `Bedrock/TimeoutSeconds` (120), `Worker/MaxConcurrency` (2), `Worker/LeaseSeconds` (300, **không vượt** `VisibilityTimeout`), `Worker/HeartbeatSeconds` (60), `Worker/JobTimeoutMinutes` (10), `Worker/RecoveryIntervalSeconds` (60), `Worker/RequeueAfterMinutes` (10, phải > backoff tối đa 300s), `Worker/StaleQueuedWarnMinutes` (15), `AiGeneration/VerifyAnswers` (`false` khi dev, `true` khi demo).
  - Thiếu `Sqs/AiJobsQueueUrl` hoặc `Bedrock/ModelId` ngoài môi trường Development → Worker dừng ngay khi khởi động (systemd báo lỗi), không chạy im lặng.
  - Message có body không đọc được `jobId` bị **xóa ngay** (log cảnh báo). Lỗi tạm thời ở lần thử cuối do Worker tự đặt job `Failed` và xóa message, khi Worker dừng để deploy thì job được gửi lại bằng message mới, nên **DLQ chủ yếu chỉ nhận message của Worker chết giữa chừng** (hoặc message trùng của job chạy rất lâu) → alarm DLQ có ý nghĩa thật.
  - Converse API dùng quyền `bedrock:InvokeModel`; nếu `Bedrock/ModelId` là inference profile thì policy phải có ARN inference profile tương ứng (đã có biến trong policy mẫu).
  - Log để theo dõi: `AI worker heartbeat.` mỗi 60s (không thấy → worker treo/chết); `AI jobs have been Queued for more than 15 minutes` (mức Warning) → kiểm tra worker/SQS.
- **Quyền IAM tối thiểu cho EC2 Instance Profile (Task `O3`):**
  - Xem statement `SqsJobQueue` trong [`infra/iam/exammaker-ec2-policy.example.json`](../../infra/iam/exammaker-ec2-policy.example.json): chỉ cấp `sqs:SendMessage`, `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:ChangeMessageVisibility`, `sqs:GetQueueAttributes` giới hạn trên `${SQS_QUEUE_ARN}` (giá trị lấy từ `/exammaker/dev/Sqs/AiJobsQueueArn`).
  - **Không** cấp quyền `sqs:PurgeQueue` hay `sqs:DeleteQueue` cho EC2 runtime.
- **Giám sát (Task `O13`):** Sử dụng `/exammaker/dev/Sqs/AiJobsDlqArn` để gắn CloudWatch Alarm khi `ApproximateNumberOfMessagesVisible > 0` trên DLQ.

---

## 5. Quy trình kiểm thử (Verification)

```bash
# Kiểm tra tự động 12 tiêu chí cấu hình, bảo mật & chi phí (an toàn chạy mọi lúc)
./infra/sqs/verify-sqs.sh

# CHỈ chạy SMOKE_TEST=1 trước khi deploy Worker (B6) hoặc khi đã dừng exammaker-worker!
SMOKE_TEST=1 ./infra/sqs/verify-sqs.sh
```

> **Cảnh báo với `SMOKE_TEST=1`:** Tuyệt đối **không** chạy `SMOKE_TEST=1` khi Worker (B6) đang chạy trên EC2 (`sudo systemctl stop exammaker-worker` trước nếu cần test lại), vì Worker đang long-poll có thể nhận mất message mẫu không có `jobId` (gây fail hoặc rơi vào DLQ kích hoạt alarm O13), hoặc smoke test nhận nhầm message thật làm tăng `ApproximateReceiveCount`.

Script xác minh đầy đủ **12 tiêu chí** (và in thêm thông tin độ sâu DLQ hiện tại):
1. Đủ 4 tham số SSM dưới `/exammaker/<env>/Sqs/` (`AiJobsQueueUrl`, `AiJobsQueueArn`, `AiJobsDlqUrl`, `AiJobsDlqArn`).
2. `AiJobsQueueArn` trong SSM khớp với ARN thực tế và kết thúc bằng `:exammaker-<env>-ai-jobs`.
3. `AiJobsDlqArn` trong SSM khớp với ARN thực tế và kết thúc bằng `:exammaker-<env>-ai-jobs-dlq`.
4. Cả 2 queue đều là **Standard Queue** (`FifoQueue=false`).
5. `RedrivePolicy` trên Main Queue trỏ chính xác sang DLQ với `maxReceiveCount = 3`.
6. `VisibilityTimeout >= 300` giây trên Main Queue.
7. `ReceiveMessageWaitTimeSeconds = 20` giây (Long Polling) trên Main Queue.
8. `MessageRetentionPeriod >= 4 ngày` trên Main Queue.
9. `MessageRetentionPeriod = 14 ngày` trên DLQ.
10. Mã hóa `SqsManagedSseEnabled = true` trên Main Queue.
11. Mã hóa `SqsManagedSseEnabled = true` trên DLQ.
12. Cả 2 queue gắn đủ tag `Project=ai-exam-maker`.

---

## 6. Vận hành & Điều tra khi có message rơi vào DLQ

Khi CloudWatch Alarm (O13) báo `DLQ depth > 0`:

1. **Xem số lượng message đang nằm trong DLQ:**
   ```bash
   DLQ_URL="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Sqs/AiJobsDlqUrl --query Parameter.Value --output text)"
   aws sqs get-queue-attributes --region ap-southeast-1 --queue-url "$DLQ_URL" \
     --attribute-names ApproximateNumberOfMessages ApproximateNumberOfMessagesNotVisible
   ```
2. **Đọc nội dung message mẫu (không xóa) để lấy `jobId` và đối chiếu bảng `ai_generation_jobs` (`status`, `attempt`, `error_code`, `error_message`) + log Worker:**
   ```bash
   aws sqs receive-message --region ap-southeast-1 --queue-url "$DLQ_URL" \
     --max-number-of-messages 5 --visibility-timeout 30
   ```
   *(Lưu ý: lệnh trên sẽ tạm ẩn message đã đọc trong **30 giây**; trong khoảng 30 giây này, AWS Console sẽ hiển thị message ở cột **Messages in flight** chứ không phải **Messages available**.)*
3. **Hướng xử lý sau điều tra (KHÔNG sửa tay DB và KHÔNG redrive mù):**
   - **DLQ dùng cho điều tra sự cố (forensics):** Theo thiết kế `docs/context.md` §4.1.1 (B6), message chỉ rơi vào DLQ sau 3 lần nhận — khi đó `attempt` trong DB thường đã đạt `max_attempts = 3` (hoặc job đã ở trạng thái `Failed`/`Succeeded`), nên đẩy lại thẳng về Main Queue (redrive) sẽ bị Worker bỏ qua (no-op).
   - **Cấm sửa tay cột `status`/`attempt` trong PostgreSQL:** Mọi chuyển trạng thái chỉ được đi qua state machine ở tầng `Application` (có audit log và đảm bảo ràng buộc `CHECK` theo `docs/context.md` §8.1, §8.4).
   - **Chạy lại tác vụ:** Sau khi đã khắc phục nguyên nhân gốc (ví dụ lỗi Bedrock/mạng/IAM), giáo viên tạo một yêu cầu sinh câu mới (có tính quota bình thường), hoặc chờ đội ngũ chốt thêm endpoint admin retry trong B6 (nếu cần, phải cập nhật `docs/context.md` trước).
   - Sau khi đã ghi nhận đủ thông tin điều tra, có thể xóa message đã xử lý khỏi DLQ (hoặc để tự hết hạn sau 14 ngày) để đưa DLQ depth về `0`.
