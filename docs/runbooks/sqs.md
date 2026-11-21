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

**Hành vi `create-if-absent`:**
- Tạo DLQ `exammaker-dev-ai-jobs-dlq` trước để lấy ARN, sau đó tạo Main Queue `exammaker-dev-ai-jobs` gắn sẵn `RedrivePolicy` (`maxReceiveCount=3`).
- Nếu queue đã tồn tại: tái sử dụng queue hiện có và cập nhật lại 4 tham số metadata lên SSM Parameter Store. Để kiểm tra độ sai lệch cấu hình (drift), chạy `./infra/sqs/verify-sqs.sh`.
- Chỉ coi lỗi `NonExistentQueue` / `QueueDoesNotExist` là chưa có queue; mọi lỗi khác (`AccessDenied`, `Throttling`) sẽ dừng script ngay và in lỗi thật.

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
- **Quyền IAM tối thiểu cho EC2 Instance Profile (Task `O3`):**
  - Xem statement `SqsJobQueue` trong [`infra/iam/exammaker-ec2-policy.example.json`](../../infra/iam/exammaker-ec2-policy.example.json): chỉ cấp `sqs:SendMessage`, `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:ChangeMessageVisibility`, `sqs:GetQueueAttributes` giới hạn trên `${SQS_QUEUE_ARN}` (giá trị lấy từ `/exammaker/dev/Sqs/AiJobsQueueArn`).
  - **Không** cấp quyền `sqs:PurgeQueue` hay `sqs:DeleteQueue` cho EC2 runtime.
- **Giám sát (Task `O13`):** Sử dụng `/exammaker/dev/Sqs/AiJobsDlqArn` để gắn CloudWatch Alarm khi `ApproximateNumberOfMessagesVisible > 0` trên DLQ.

---

## 5. Quy trình kiểm thử (Verification)

```bash
# Kiểm tra tự động 12 tiêu chí cấu hình, bảo mật & chi phí
./infra/sqs/verify-sqs.sh

# Kiểm tra 12 tiêu chí + chạy smoke test gửi -> nhận -> xóa message mẫu (chỉ chạy khi Worker chưa poll queue)
SMOKE_TEST=1 ./infra/sqs/verify-sqs.sh
```

Script xác minh đầy đủ **12 tiêu chí**:
1. Đủ 4 tham số SSM dưới `/exammaker/<env>/Sqs/` (`AiJobsQueueUrl`, `AiJobsQueueArn`, `AiJobsDlqUrl`, `AiJobsDlqArn`).
2. `AiJobsQueueArn` trong SSM khớp với ARN thực tế của Main Queue.
3. `AiJobsDlqArn` trong SSM khớp với ARN thực tế của DLQ.
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

## 6. Vận hành & Xử lý khi có message rơi vào DLQ

Khi CloudWatch Alarm (O13) báo `DLQ depth > 0`:

1. **Xem số lượng message đang nằm trong DLQ:**
   ```bash
   DLQ_URL="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Sqs/AiJobsDlqUrl --query Parameter.Value --output text)"
   aws sqs get-queue-attributes --region ap-southeast-1 --queue-url "$DLQ_URL" \
     --attribute-names ApproximateNumberOfMessages ApproximateNumberOfMessagesNotVisible
   ```
2. **Đọc nội dung message mẫu (không xóa) để lấy `jobId` và đối chiếu bảng `ai_generation_jobs` + log Worker:**
   ```bash
   aws sqs receive-message --region ap-southeast-1 --queue-url "$DLQ_URL" \
     --max-number-of-messages 5 --visibility-timeout 30
   ```
3. **Đẩy lại message về Main Queue (Start DLQ Redrive) sau khi đã khắc phục nguyên nhân gốc:**
   - Qua AWS Console: *SQS → `exammaker-dev-ai-jobs-dlq` → Start DLQ redrive → Redrive to source queue*.
   - Lưu ý: Worker có cơ chế idempotent theo `jobId` và kiểm tra `attempt < max_attempts`, vì vậy nếu job đã được đánh dấu `Failed` trong DB thì cần reset trạng thái job hợp lệ trước khi redrive.
