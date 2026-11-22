# Runbook — Quy ước & Vận hành AWS Systems Manager Parameter Store (Task O12)

Tài liệu quy định cấu trúc cây tham số tập trung tại **AWS Systems Manager Parameter Store** cho dự án **AI Exam Maker** (`ap-southeast-1`), đảm bảo:
- **100% ánh xạ tự động vào .NET 10 `IConfiguration`** qua `ParameterStoreConfiguration.cs` khi cấu hình `ParameterStore__Path=/exammaker/<env>`.
- **Chi phí $0**: toàn bộ tham số dùng **Standard tier** (`≤ 10.000` tham số miễn phí) và mọi `SecureString` mã hóa bằng khóa **AWS-managed `alias/aws/ssm`** (không tạo KMS CMK tốn $1/tháng).
- **Bảo mật**: tuyệt đối không lưu secret, connection string, Account ID hay giá trị thật trong Git hoặc file `appsettings.json`.

---

## 1. Quy ước đặt tên (Naming Convention)

```text
/exammaker/<env>/<Section>/<Key>
```

- `<env>`: môi trường triển khai (`dev` cho giai đoạn phát triển & demo).
- `<Section>/<Key>`: khớp chính xác với phân cấp `Section:Key` trong .NET `IConfiguration` (ví dụ `/exammaker/dev/AiQuota/JobsPerUserPerDay` → `AiQuota:JobsPerUserPerDay`).
- Phân loại kiểu dữ liệu:
  - **`SecureString`** (bắt buộc với khóa `alias/aws/ssm`): mọi tham số chứa chuỗi kết nối có mật khẩu (`ConnectionStrings/*`), mật khẩu (`*Password*`), hoặc chuỗi bí mật (`*Secret*`).
  - **`String`**: toàn bộ cấu hình không bí mật (Region, ModelId, Quota, Cognito IDs, SQS URLs, CloudWatch metadata).

---

## 2. Bản đồ toàn bộ cây tham số `/exammaker/dev/*`

| Nhóm / Task | Đường dẫn SSM | Kiểu | Giá trị mặc định / Nguồn | Nơi sử dụng |
|---|---|---|---|---|
| **AWS Core (O12)** | `/exammaker/dev/AWS/Region` | `String` | `ap-southeast-1` | `AWS:Region` (`AiServiceCollectionExtensions.cs`) |
| **Cognito (O9)** | `/exammaker/dev/Cognito/Region` | `String` | `ap-southeast-1` | `Cognito:Region` (`CognitoOptions.cs`) |
| | `/exammaker/dev/Cognito/UserPoolId` | `String` | Do O9 tạo | `Cognito:UserPoolId` (`CognitoOptions.cs`) |
| | `/exammaker/dev/Cognito/ClientId` | `String` | Do O9 tạo | `Cognito:ClientId` (`CognitoOptions.cs`) |
| | `/exammaker/dev/Cognito/HostedUiDomain` | `String` | Do O9 tạo | Frontend / O8 |
| | `/exammaker/dev/Cognito/Authority` | `String` | Do O9 tạo | JWT issuer / O8 |
| **RDS (O10)** | `/exammaker/dev/ConnectionStrings/ExamMaker` | **`SecureString`** | Do O10 tạo (`SSL Mode=Require`) | `ConnectionStrings:ExamMaker` (`PersistenceServiceCollectionExtensions.cs`) |
| | `/exammaker/dev/Rds/{InstanceId,Endpoint,Port,Database,Username}` | `String` | Do O10 tạo | OPS / CD migration (O15) |
| **SQS (O11)** | `/exammaker/dev/Sqs/AiJobsQueueUrl` | `String` | Do O11 tạo | `Sqs:AiJobsQueueUrl` (`AiServiceCollectionExtensions.cs`) |
| | `/exammaker/dev/Sqs/{AiJobsQueueArn,AiJobsDlqUrl,AiJobsDlqArn}` | `String` | Do O11 tạo | EC2 IAM (O3), CloudWatch (O13) |
| **Bedrock (O12)** | `/exammaker/dev/Bedrock/Region` | `String` | `ap-southeast-1` | `Bedrock:Region` (Worker B6) |
| | `/exammaker/dev/Bedrock/ModelId` | `String` | `apac.anthropic.claude-3-haiku-20240307-v1:0` | `Bedrock:ModelId` (model đang chạy: Claude 3 Haiku cho dev, đổi sang Sonnet v2 khi demo) |
| | `/exammaker/dev/Bedrock/DevModelId` | `String` | `apac.anthropic.claude-3-haiku-20240307-v1:0` | Tham chiếu model dev/tiết kiệm chi phí (Claude 3 Haiku) để rollback sau demo |
| | `/exammaker/dev/Bedrock/DemoModelId` | `String` | `apac.anthropic.claude-3-5-sonnet-20241022-v2:0` | Tham chiếu model demo (Claude 3.5 Sonnet v2) |
| | `/exammaker/dev/Bedrock/EnableAnswerVerification` | `String` | `false` (`true` khi demo) | `Bedrock:EnableAnswerVerification` (`docs/context.md` §14) |
| **AI Quota (O12)** | `/exammaker/dev/AiQuota/JobsPerUserPerDay` | `String` | `10` | `AiQuota:JobsPerUserPerDay` (`AiQuotaOptions.cs`) |
| | `/exammaker/dev/AiQuota/MaxQuestionsPerJob` | `String` | `10` | `AiQuota:MaxQuestionsPerJob` (`AiQuotaOptions.cs`) |
| | `/exammaker/dev/AiQuota/TokensPerUserPerDay` | `String` | `100000` | `AiQuota:TokensPerUserPerDay` (`AiQuotaOptions.cs`) |
| | `/exammaker/dev/AiQuota/SystemTokensPerDay` | `String` | `500000` | `AiQuota:SystemTokensPerDay` (`AiQuotaOptions.cs`) |
| | `/exammaker/dev/AiQuota/MaxNoteLength` | `String` | `1000` | `AiQuota:MaxNoteLength` (`AiQuotaOptions.cs`) |
| | `/exammaker/dev/AiQuota/MaxAttempts` | `String` | `3` | `AiQuota:MaxAttempts` (`AiQuotaOptions.cs`, khớp SQS `maxReceiveCount=3`) |
| **AI Rate Limit (O12)** | `/exammaker/dev/AiRateLimit/PermitLimit` | `String` | `5` | `AiRateLimit:PermitLimit` (`AiRateLimiting.cs`) |
| | `/exammaker/dev/AiRateLimit/Window` | `String` | `00:01:00` | `AiRateLimit:Window` (`AiRateLimiting.cs`) |
| **Origin Secret (O12)** | `/exammaker/dev/Origin/VerifySecret` | **`SecureString`** | Tự sinh 64 ký tự hex (256-bit) | Header `X-Origin-Verify` trên CloudFront (O5) và `/etc/exammaker/origin-secret.conf` trên Nginx (O4) |
| **CloudWatch (O12)** | `/exammaker/dev/CloudWatch/LogGroupName` | `String` | `/exammaker/dev/app` | IAM policy EC2 (O3), CloudWatch (O13) |
| | `/exammaker/dev/CloudWatch/MetricNamespace` | `String` | `ExamMaker/Dev` | IAM policy EC2 (O3), CloudWatch (O13) |

---

## 3. Triển khai & Kiểm tra tự động

### 3.1 Dựng tham số O12 (create-if-absent)

```bash
./infra/ssm/setup-ssm.sh
```

**Hành vi an toàn của script:**
- Các tham số cấu hình (`Bedrock/*`, `AiQuota/*`, `AiRateLimit/*`, `AWS/Region`, `CloudWatch/*`) mặc định là **create-if-absent** để tránh vô tình ghi đè khi team đã chuyển sang cấu hình demo (ví dụ đã bật Sonnet v2). Muốn đồng bộ lại theo mặc định script, chạy `OVERWRITE_CONFIG=1 ./infra/ssm/setup-ssm.sh`.
- `/exammaker/dev/Origin/VerifySecret` **không bao giờ bị ghi đè** ở lần chạy lại bình thường (tránh làm lệch secret giữa CloudFront và Nginx gây lỗi `403`). Chỉ xoay vòng khi truyền rõ `ROTATE_ORIGIN_SECRET=1` hoặc `ORIGIN_VERIFY_SECRET=<value>`.

### 3.2 Xác minh toàn bộ cây tham số (O9 + O10 + O11 + O12)

```bash
./infra/ssm/verify-ssm.sh
```

Script kiểm tra tự động **18 tiêu chí** cho **32 tham số chuẩn hóa** (5 O9 + 6 O10 + 4 O11 + 17 O12; riêng `Rds/MasterPassword` của O10 là tham số nội bộ OPS nên không liệt kê vào danh sách app config):
1. Đủ nhóm tham số O9 (Cognito), O10 (RDS connection string + metadata), O11 (SQS + DLQ), O12 (AWS, Bedrock gồm `ModelId`/`DevModelId`/`DemoModelId`, AI Quota, AI Rate Limit, Origin Secret, CloudWatch).
2. Xác thực trực tiếp `Bedrock/ModelId`, `Bedrock/DevModelId`, `Bedrock/DemoModelId` là inference profile `ACTIVE` (hoặc foundation model `ON_DEMAND`) tại `Bedrock/Region`.
3. `ConnectionStrings/ExamMaker` và `Origin/VerifySecret` bắt buộc là `SecureString` (và `Origin/VerifySecret` ≥ 32 ký tự); tuyệt đối không in giá trị secret ra terminal.
4. Các giá trị quota là số nguyên dương hợp lệ, `MaxAttempts == 3` khớp SQS `maxReceiveCount=3`, `SystemTokensPerDay >= TokensPerUserPerDay`, và `AiRateLimit/Window` là `.NET TimeSpan > 0`.
5. **Kiểm soát chi phí ($0):** 100% tham số dưới `/exammaker/dev/` dùng **Standard tier** và 100% `SecureString` dùng khóa AWS-managed `alias/aws/ssm`.
6. **Kiểm soát bảo mật:** không có tham số nào chứa `Secret`, `Password` hoặc `ConnectionStrings` bị lưu ở dạng `String` thuần.

---

## 4. Vận hành thường ngày

### 4.1 Nạp cấu hình trên EC2 (API & Worker)

Đặt biến môi trường trong `/etc/exammaker/api.env` và `/etc/exammaker/worker.env`:

```bash
ParameterStore__Path=/exammaker/dev
```

Instance profile của EC2 (`aem-exammaker-ec2-role`, xem `infra/iam/exammaker-ec2-policy.example.json`) chỉ cần quyền `ssm:GetParameter`, `ssm:GetParameters`, `ssm:GetParametersByPath` giới hạn đúng 2 ARN `arn:aws:ssm:ap-southeast-1:<account-id>:parameter/exammaker/dev` và `arn:aws:ssm:ap-southeast-1:<account-id>:parameter/exammaker/dev/*` (khớp `${PARAMETER_ARN_PREFIX}` và `${PARAMETER_ARN_PREFIX}/*` trong policy mẫu; khóa `alias/aws/ssm` được giải mã mặc định mà không cần cấp `kms:Decrypt` riêng cho CMK).

### 4.2 Chuyển đổi cấu hình Dev (Haiku) ↔ Demo (Sonnet v2)

**Trước buổi demo (chuyển sang Sonnet v2 + bật kiểm chứng đáp án):**

```bash
DEMO_MODEL="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/DemoModelId --query Parameter.Value --output text)"
aws ssm put-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/ModelId --type String --value "$DEMO_MODEL" --overwrite
aws ssm put-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/EnableAnswerVerification --type String --value "true" --overwrite
```

Sau đó restart dịch vụ trên EC2 qua SSM Session Manager để nạp cấu hình mới:

```bash
sudo systemctl restart exammaker-api exammaker-worker
```

**Sau buổi demo (đưa về `DevModelId` + tắt kiểm chứng để tiết kiệm chi phí):**

```bash
DEV_MODEL="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/DevModelId --query Parameter.Value --output text)"
aws ssm put-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/ModelId --type String --value "$DEV_MODEL" --overwrite
aws ssm put-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/EnableAnswerVerification --type String --value "false" --overwrite
```

### 4.3 Lấy `Origin/VerifySecret` để cấu hình Nginx (O4) & CloudFront (O5)

Trên EC2 (qua SSM Session Manager), lấy secret trực tiếp từ Parameter Store để ghi vào `/etc/exammaker/origin-secret.conf` (không sao chép qua chat):

```bash
SECRET="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Origin/VerifySecret --with-decryption --query Parameter.Value --output text)"
sudo install -o root -g nginx -m 0640 /dev/null /etc/exammaker/origin-secret.conf
sudo tee /etc/exammaker/origin-secret.conf >/dev/null <<EOF
map \$http_x_origin_verify \$origin_header_valid {
    default 0;
    "${SECRET}" 1;
}
EOF
unset SECRET
sudo nginx -t && sudo systemctl reload nginx
```

> **Quy trình xoay vòng secret không downtime (2 bước — khi chạy `ROTATE_ORIGIN_SECRET=1 ./infra/ssm/setup-ssm.sh`):**
> 1. Trên Nginx (EC2), cập nhật `/etc/exammaker/origin-secret.conf` để tạm thời chấp nhận **cả secret cũ và secret mới** (`"${OLD_SECRET}" 1;` và `"${NEW_SECRET}" 1;`) rồi `sudo nginx -t && sudo systemctl reload nginx`.
> 2. Cập nhật custom header `X-Origin-Verify` trên CloudFront Origin (O5) sang secret mới và chờ CloudFront chuyển sang trạng thái `Deployed`.
> 3. Gỡ dòng `"${OLD_SECRET}" 1;` khỏi `/etc/exammaker/origin-secret.conf` và reload Nginx lại.
