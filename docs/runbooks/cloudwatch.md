# Runbook — Thiết lập & Vận hành Giám sát Amazon CloudWatch (Task O13)

Tài liệu hướng dẫn thiết lập, xác minh và quy trình xử lý sự cố (incident playbook) cho hệ thống **CloudWatch Logs, Metric Filters, SNS Alerts và CloudWatch Alarms** của dự án **AI Exam Maker** theo `docs/context.md` (mục 5.2, 6, 10) và `docs/tasks.md` (O13).

> **Quy tắc bảo mật & chi phí:**
> - Tuyệt đối **không** commit địa chỉ email cá nhân, AWS Account ID, EC2 Instance ID (`i-...`), EBS Volume ID (`vol-...`) hoặc ARN thật vào repository.
> - Giữ tổng số alarm toàn region **≤ 10 Standard-Resolution Alarms** (`Period = 300s`), tổng số custom metric trong namespace `ExamMaker/Dev` **≤ 10**, và thời hạn lưu log **`retentionInDays = 7`** để nằm trọn trong **AWS Free Tier ($0.00/tháng)**.

---

## 1. Kiến trúc Giám sát & Định mức Free Tier

### 1.1 CloudWatch Log Group & 4 Metric Filters
- **Log Group:** `/exammaker/dev/app` (đọc từ SSM `/exammaker/dev/CloudWatch/LogGroupName`).
- **Retention:** **`7` ngày** (`STANDARD` log group class, mã hóa mặc định của AWS = **$0.00** cho 5 GB/tháng đầu tiên).
- **4 Metric Filters** tự động chuyển dòng log của Serilog (`ExamMaker.Api` & `ExamMaker.Worker`) thành metric trong namespace **`ExamMaker/Dev`** (đọc từ SSM `/exammaker/dev/CloudWatch/MetricNamespace`):

| Tên Metric Filter | Filter Pattern | Metric đích (`ExamMaker/Dev`) | Ý nghĩa |
|---|---|---|---|
| `exammaker-dev-api-5xx-filter` | `?"responded 500" ?"responded 501" ?"responded 502" ?"responded 503" ?"responded 504" ?"\"StatusCode\":5"` | `Api5xxCount` | Đếm số response HTTP `5xx` từ middleware `app.UseSerilogRequestLogging()` của API |
| `exammaker-dev-worker-heartbeat-filter` | `?"WorkerHeartbeat" ?"worker_heartbeat"` | `WorkerHeartbeat` | Ghi nhận nhịp sống định kỳ của Worker từ log (song song với `cloudwatch:PutMetricData`) |
| `exammaker-dev-worker-stuck-jobs-filter` | `?"StuckRunningJob" ?"LeaseExpired" ?"stuck_running_job"` | `StuckRunningJobs` | Ghi nhận khi tác vụ phục hồi phát hiện job `Running` quá hạn lease |
| `exammaker-dev-worker-errors-filter` | `?"[ERR]" ?"[FTL]" ?"\"Level\":\"Error\"" ?"\"Level\":\"Fatal\""` | `WorkerErrorCount` | Đếm lỗi `[ERR]` / `[FTL]` từ Worker và ứng dụng |

### 1.2 Danh mục 10 CloudWatch Alarms (Đúng trần 10/10 Free Tier = $0.00/tháng)

Tất cả 10 alarm đều dùng **Standard Resolution (`Period = 300s` = 5 phút)** và gửi cảnh báo qua SNS Topic **`exammaker-dev-alerts`**:

| # | Tên Alarm | Nhóm | Namespace :: Metric | Điều kiện kích hoạt | `TreatMissingData` |
|---|---|---|---|---|---|
| 1 | `exammaker-dev-sqs-dlq-depth` | **Bắt buộc #1** | `AWS/SQS` :: `ApproximateNumberOfMessagesVisible` (`exammaker-dev-ai-jobs-dlq`) | `Maximum > 0` (1 kỳ 5 phút) | `notBreaching` |
| 2 | `exammaker-dev-api-5xx` | **Bắt buộc #2** | `ExamMaker/Dev` :: `Api5xxCount` | `Sum >= 5` (1 kỳ 5 phút) | `notBreaching` |
| 3 | `exammaker-dev-worker-heartbeat` | **Bắt buộc #3** | `ExamMaker/Dev` :: `WorkerHeartbeat` | `Sum < 1` (1 kỳ 5 phút) | **`breaching`** |
| 4 | `exammaker-dev-sqs-oldest-msg` | Pipeline AI | `AWS/SQS` :: `ApproximateAgeOfOldestMessage` (`exammaker-dev-ai-jobs`) | `Maximum > 600` giây (10 phút) | `notBreaching` |
| 5 | `exammaker-dev-worker-stuck-jobs` | Pipeline AI | `ExamMaker/Dev` :: `StuckRunningJobs` | `Maximum > 0` (1 kỳ 5 phút) | `notBreaching` |
| 6 | `exammaker-dev-worker-errors` | Pipeline AI | `ExamMaker/Dev` :: `WorkerErrorCount` | `Sum >= 3` (1 kỳ 5 phút) | `notBreaching` |
| 7 | `exammaker-dev-ec2-cpu-high` | Hạ tầng EC2 | `AWS/EC2` :: `CPUUtilization` (tag `Name=aem-exammaker-ec2`) | `Average > 80%` (2 kỳ = 10 phút) | `notBreaching` |
| 8 | `exammaker-dev-ebs-stalled-io` | Hạ tầng EBS | `AWS/EBS` :: `VolumeStalledIOCheck` (root `gp3` của EC2) | `Maximum > 0` (1 kỳ 5 phút) | `notBreaching` |
| 9 | `exammaker-dev-rds-storage-low` | Hạ tầng RDS | `AWS/RDS` :: `FreeStorageSpace` (`exammaker-dev-pg`) | `Minimum < 2147483648` (< 2 GB) | `notBreaching` |
| 10 | `exammaker-dev-rds-connections-high` | Hạ tầng RDS | `AWS/RDS` :: `DatabaseConnections` (`exammaker-dev-pg`) | `Average > 50` (1 kỳ 5 phút) | `notBreaching` |

---

## 2. Cách dựng & đồng bộ bằng script tự động

**Yêu cầu công cụ:** `aws` (CLI v2), `jq` — cài sẵn trong **AWS CloudShell** (tại `ap-southeast-1`).

```bash
# Cách A (khuyên dùng trên máy dùng chung để tránh lưu email trong lịch sử shell):
ALERT_EMAILS_STDIN=1 ./infra/cloudwatch/setup-cloudwatch.sh
# (dán chuỗi devA@example.com,devB@example.com,ops1@example.com,ops2@example.com rồi nhấn Enter)

# Cách B (nếu SNS Topic đã có sẵn email subscriber, chỉ cần chạy lại để đồng bộ log group/filter/alarm):
./infra/cloudwatch/setup-cloudwatch.sh
```

> **Lưu ý xác nhận email SNS:** Sau lần đăng ký đầu tiên, AWS SNS sẽ gửi email có tiêu đề *"AWS Notification - Subscription Confirmation"* tới từng thành viên. Thành viên cần bấm **"Confirm subscription"** trong hộp thư để bắt đầu nhận cảnh báo khi alarm chuyển sang trạng thái `ALARM`.

---

## 3. Xác minh tự động 15 tiêu chí (Verification)

```bash
# Kiểm tra đầy đủ 15 tiêu chí (mặc định yêu cầu >= 1 email subscription trên SNS Topic)
./infra/cloudwatch/verify-cloudwatch.sh

# Kiểm tra chặt đã đăng ký đủ cả 4 thành viên trong nhóm
MIN_SUBSCRIBERS=4 ./infra/cloudwatch/verify-cloudwatch.sh
```

Script `verify-cloudwatch.sh` kiểm tra tự động **15 tiêu chí** (không in email, Account ID hay EC2/EBS ID ra màn hình):
1. Đủ 2 tham số SSM `/exammaker/dev/CloudWatch/LogGroupName` và `MetricNamespace`.
2. Log Group `/exammaker/dev/app` tồn tại, `retentionInDays = 7`, `STANDARD` class, không tốn phí KMS CMK.
3. Đủ 4 Metric Filters trên `/exammaker/dev/app` trỏ về namespace `ExamMaker/Dev`.
4. SNS Topic `exammaker-dev-alerts` tồn tại và có đủ số lượng email subscription.
5. **5–14:** Đủ 10 CloudWatch Alarms với `ActionsEnabled = true`, gắn đúng `AlarmActions` vào SNS Topic, đúng `Period = 300s`, ngưỡng, toán tử và dimensions thực tế của SQS, EC2, EBS, RDS.
6. **15 (Cost Guardrail):** Tổng số Metric Alarms trong region `<= 10`, `0` Composite Alarm, `0` High-Resolution Alarm, và số custom metric trong `ExamMaker/Dev` `<= 10`.

---

## 4. Tích hợp với Backend (API & Worker B6) và EC2 (O3/O15)

1. **Quyền IAM trên EC2 (`infra/iam/exammaker-ec2-policy.example.json`):**
   - Instance profile của EC2 đã được thiết kế sẵn 2 statement least-privilege:
     - `WriteApplicationLogs`: cho phép `logs:CreateLogStream`, `logs:DescribeLogStreams`, `logs:PutLogEvents` vào `/exammaker/dev/app`.
     - `PublishApplicationMetrics`: cho phép `cloudwatch:PutMetricData` có điều kiện `cloudwatch:namespace == "ExamMaker/Dev"`.
2. **Cách Worker (B6) phát Heartbeat & báo cáo Job kẹt:**
   - **Cách 1 (Gọi trực tiếp CloudWatch SDK hoặc CLI):** Định kỳ mỗi 1–2 phút, Worker đẩy metric `WorkerHeartbeat = 1` (`Unit = Count`) lên namespace `ExamMaker/Dev`; khi vòng quét phát hiện job `Running` quá hạn lease, đẩy `StuckRunningJobs = <count>`.
   - **Cách 2 (Qua CloudWatch Logs Metric Filter đã cài sẵn):** Khi log của `exammaker-api` và `exammaker-worker` được đẩy lên Log Group `/exammaker/dev/app`, bất kỳ dòng log nào chứa chuỗi `WorkerHeartbeat` sẽ tự động cộng `+1` vào metric `ExamMaker/Dev :: WorkerHeartbeat`, và dòng log chứa `StuckRunningJob` / `LeaseExpired` sẽ tự động cộng `+1` vào `ExamMaker/Dev :: StuckRunningJobs`.
3. **Lưu ý về trạng thái của `exammaker-dev-worker-heartbeat` trước khi deploy B6 & trong khung giờ tắt máy ban đêm (23:00–07:00):**
   - Vì `exammaker-dev-worker-heartbeat` cấu hình `TreatMissingData = breaching` (đúng yêu cầu *"không có heartbeat → cảnh báo"* tại `docs/context.md` §10), khi Worker B6 chưa được deploy hoặc khi EC2 dừng ban đêm (23:00–07:00), alarm này sẽ chuyển sang trạng thái `ALARM` **một lần duy nhất** (CloudWatch chỉ gửi email SNS khi chuyển trạng thái `OK → ALARM`, không gửi lặp lại mỗi 5 phút).
   - Nếu muốn tạm tắt gửi email cảnh báo heartbeat trước khi tắt EC2 ban đêm và bật lại vào buổi sáng:
     ```bash
     # Trước khi dừng EC2 ban đêm:
     aws cloudwatch disable-alarm-actions --region ap-southeast-1 --alarm-names exammaker-dev-worker-heartbeat

     # Sau khi bật lại EC2 và Worker vào buổi sáng:
     aws cloudwatch enable-alarm-actions --region ap-southeast-1 --alarm-names exammaker-dev-worker-heartbeat
     ```

---

## 5. Playbook xử lý sự cố khi Alarm kích hoạt

| Alarm kích hoạt | Nguyên nhân thường gặp | Các bước kiểm tra & khắc phục |
|---|---|---|
| **`exammaker-dev-sqs-dlq-depth`** | Job sinh câu hỏi AI thất bại cả 3 lần thử (`maxReceiveCount = 3`) do lỗi Bedrock, vượt quota token, hoặc output JSON của model sai schema sau vòng sửa. | 1. Xem log lỗi trên CloudWatch Logs `/exammaker/dev/app` hoặc `journalctl -u exammaker-worker -n 100` qua EC2 SSM.<br>2. Kiểm tra bảng `ai_generation_jobs` (`status = 'Failed'`, cột `error_code` / `error_message`).<br>3. Làm theo quy trình xử lý DLQ tại [`docs/runbooks/sqs.md`](sqs.md) (mục 6). |
| **`exammaker-dev-api-5xx`** | API trả `>= 5` lỗi HTTP 500/502/503/504 trong 5 phút (lỗi kết nối RDS, timeout khóa DB, hoặc exception chưa bắt). | 1. Truy vấn `/exammaker/dev/app` lọc theo `"responded 5"` hoặc `traceId`.<br>2. Kiểm tra trạng thái RDS (`exammaker-dev-pg`) và `systemctl status exammaker-api` trên EC2. |
| **`exammaker-dev-worker-heartbeat`** | Process `exammaker-worker` bị dừng, crash lặp, hoặc treo vòng lặp không phát được heartbeat trong 5 phút. | 1. Mở SSM Session Manager vào EC2: chạy `sudo systemctl status exammaker-worker` và `sudo journalctl -u exammaker-worker -n 100`.<br>2. Khởi động lại service: `sudo systemctl restart exammaker-worker`. |
| **`exammaker-dev-sqs-oldest-msg`** | Message nằm chờ trong `exammaker-dev-ai-jobs` quá 10 phút (> 600s) do Worker đang dừng hoặc xử lý quá chậm. | 1. Kiểm tra `exammaker-worker` có đang `active (running)` không.<br>2. Kiểm tra độ trễ Bedrock hoặc số lượng message tồn đọng trong Main Queue. |
| **`exammaker-dev-worker-stuck-jobs`** | Có job AI ở trạng thái `Running` vượt quá thời hạn lease (Worker bị kill/OOM giữa chừng khi đang gọi Bedrock). | 1. Kiểm tra `journalctl -u exammaker-worker` xem có bị OOM (`MemoryMax=1024M` trong `exammaker-worker.service`) không.<br>2. Xác nhận tác vụ phục hồi của Worker đã chuyển job về `Queued` hoặc `Failed` an toàn. |
| **`exammaker-dev-ec2-cpu-high`** / **`ebs-stalled-io`** | CPU EC2 `t4g.small` > 80% liên tục 10 phút hoặc ổ đĩa EBS `gp3` bị nghẽn I/O. | 1. Vào EC2 qua SSM chạy `top` / `htop` và `iostat -xz 1 5`.<br>2. Kiểm tra xem có tiến trình nào lặp vô hạn hoặc đầy RAM gây thrashing swap/page cache không. |
| **`exammaker-dev-rds-storage-low`** / **`rds-connections-high`** | Dung lượng trống RDS `< 2 GB` hoặc số kết nối đồng thời `> 50` (rò rỉ connection pool). | 1. Kiểm tra bảng chiếm nhiều dung lượng hoặc log PostgreSQL.<br>2. Nếu `DatabaseConnections > 50`: kiểm tra `MaxPoolSize` trong `ConnectionStrings/ExamMaker` và restart `exammaker-api` / `exammaker-worker` nếu bị treo kết nối. |
