# Runbook — Quy ước & Vận hành AWS Systems Manager Parameter Store (Task O12)

Tài liệu quy định cấu trúc cây tham số tập trung tại **AWS Systems Manager Parameter Store** cho dự án **AI Exam Maker** (`ap-southeast-1`), đảm bảo:
- **Ánh xạ tự động vào .NET 10 `IConfiguration`** qua `ParameterStoreConfiguration.cs` khi cấu hình `ParameterStore__Path=/exammaker/<env>` cho **các khóa hiện đã được bind trong code** (xem cột *Nơi sử dụng* ở mục 2: `AWS:Region`, `Cognito:Region|UserPoolId|ClientId`, `ConnectionStrings:ExamMaker`, `Sqs:AiJobsQueueUrl`, `AiQuota:*`, `AiRateLimit:*`). Các khóa `Bedrock/*` sẽ được Worker B6 bind sau; các khóa `Rds/*`, `CloudWatch/*`, `Origin/VerifySecret`, `Cognito/HostedUiDomain|Authority`, `Sqs/*Arn|Dlq*` phục vụ hạ tầng/OPS/frontend.
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
| **Bedrock (O12)** | `/exammaker/dev/Bedrock/Region` | `String` | `ap-southeast-1` | Worker B6 (chưa có code đọc ở hiện tại) |
| | `/exammaker/dev/Bedrock/ModelId` | `String` | `global.anthropic.claude-haiku-4-5-20251001-v1:0` | Worker B6 (chưa có code đọc ở hiện tại; Haiku 4.5 cho dev, đổi sang Sonnet 4.6 khi demo) |
| | `/exammaker/dev/Bedrock/DevModelId` | `String` | `global.anthropic.claude-haiku-4-5-20251001-v1:0` | Tham chiếu model dev/tiết kiệm chi phí (Claude Haiku 4.5) để rollback sau demo (B6) |
| | `/exammaker/dev/Bedrock/DemoModelId` | `String` | `global.anthropic.claude-sonnet-4-6` | Tham chiếu model demo (Claude Sonnet 4.6) (B6) |
| | `/exammaker/dev/Bedrock/EnableAnswerVerification` | `String` | `false` (`true` khi demo) | Worker B6 (chưa có code đọc ở hiện tại; `docs/context.md` §14) |
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
- Các tham số cấu hình (`Bedrock/*`, `AiQuota/*`, `AiRateLimit/*`, `AWS/Region`, `CloudWatch/*`) mặc định là **create-if-absent** để tránh vô tình ghi đè khi team đã chuyển sang cấu hình demo (ví dụ đã bật Sonnet 4.6).
  - Muốn ghi đè chỉ một hoặc vài tham số cụ thể (ví dụ chỉ đổi quota mà không đụng tới model), dùng `OVERWRITE_KEYS="AiQuota/JobsPerUserPerDay" AI_JOBS_PER_USER_PER_DAY=20 ./infra/ssm/setup-ssm.sh`.
  - Khi chạy `OVERWRITE_CONFIG=1 ./infra/ssm/setup-ssm.sh` để đồng bộ hàng loạt về mặc định script, hai tham số điều khiển runtime demo là `Bedrock/ModelId` và `Bedrock/EnableAnswerVerification` **vẫn được bảo vệ không bị reset** (trừ khi bật thêm `OVERWRITE_BEDROCK_RUNTIME=1` hoặc chỉ định rõ trong `OVERWRITE_KEYS`).
- `/exammaker/dev/Origin/VerifySecret` **không bao giờ bị ghi đè** khi đã tồn tại nếu không truyền rõ `ROTATE_ORIGIN_SECRET=1` (kể cả khi trong môi trường có sẵn `ORIGIN_VERIFY_SECRET`, giúp tránh vô tình xoay secret khi chạy lại script để đổi quota gây lỗi `403`). Khi cần xoay, ưu tiên `ROTATE_ORIGIN_SECRET=1 ./infra/ssm/setup-ssm.sh` (tự sinh secret trong bộ nhớ, không lưu lại trong shell history). Chỉ kết hợp `ROTATE_ORIGIN_SECRET=1` với `ORIGIN_VERIFY_SECRET=<value>` trên shell cá nhân không dùng chung (hoặc đọc qua stdin với `ROTATE_ORIGIN_SECRET=1 ORIGIN_VERIFY_SECRET_STDIN=1`) để tránh lộ secret trong lịch sử lệnh.

### 3.2 Xác minh toàn bộ cây tham số (O9 + O10 + O11 + O12)

```bash
./infra/ssm/verify-ssm.sh
```

Script kiểm tra tự động **19 tiêu chí** cho **32 tham số chuẩn hóa** (5 O9 + 6 O10 + 4 O11 + 17 O12; riêng `Rds/MasterPassword` của O10 là tham số bootstrap nội bộ OPS — xem lưu ý least-privilege ở mục 4.1). Lưu ý chạy bằng danh tính **OPS** có quyền `bedrock:ListInferenceProfiles`, `bedrock:ListFoundationModels` và `bedrock:GetFoundationModel` (role runtime EC2 không cần các quyền này):
1. Đủ nhóm tham số O9 (Cognito), O10 (RDS connection string + metadata), O11 (SQS + DLQ), O12 (AWS, Bedrock gồm `ModelId`/`DevModelId`/`DemoModelId`, AI Quota, AI Rate Limit, Origin Secret, CloudWatch).
2. Xác thực trực tiếp `Bedrock/ModelId`, `Bedrock/DevModelId`, `Bedrock/DemoModelId`: với inference profile (`global.*` / `apac.*`), bắt buộc **`profile.status == "ACTIVE"`** tại `Bedrock/Region` và kiểm tra thêm `modelLifecycle.status` của foundation model đích theo cơ chế best-effort (từ chối ngay nếu phát hiện `LEGACY`; nếu model đích không liệt kê ở region nguồn hoặc lệnh `get-foundation-model` bị giới hạn/throttle thì ghi chú rõ trạng thái chưa xác minh được lifecycle cục bộ nhưng vẫn chấp nhận theo `profile.status == "ACTIVE"`). Với foundation model trực tiếp (không qua profile), bắt buộc `ON_DEMAND` và `modelLifecycle.status == "ACTIVE"`. Đồng thời `ModelId` phải khớp `DevModelId` hoặc `DemoModelId`.
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

> **Lưu ý Least-Privilege cho `Rds/MasterPassword` và `Origin/VerifySecret` (tồn tại từ O10 / kiến trúc nạp prefix):** Script bootstrap RDS ở O10 lưu `/exammaker/dev/Rds/MasterPassword` và O12 lưu `/exammaker/dev/Origin/VerifySecret` dưới cùng prefix `/exammaker/dev/*`. Vì `AddSystemsManager("/exammaker/dev")` nạp đệ quy toàn bộ cây tham số dưới prefix này vào `IConfiguration` của API/Worker (`verify-ssm.sh` sẽ phát cảnh báo `[WARN]` nếu còn thấy `Rds/MasterPassword`), OPS nên chuyển mật khẩu master sang nhánh riêng ngoài prefix của app (ví dụ `/exammaker-ops/dev/Rds/MasterPassword`) hoặc xóa `/exammaker/dev/Rds/MasterPassword` sau khi đã lưu vào trình quản lý mật khẩu của team. Tương tự, có thể giới hạn resource ARN trong IAM policy của EC2 chỉ gồm các nhánh ứng dụng bind (loại trừ `Rds/MasterPassword` và `Origin/VerifySecret` khỏi process .NET nếu đọc `Origin/VerifySecret` cho Nginx bằng role/phiên OPS).

### 4.2 Chuyển đổi cấu hình Dev (Haiku 4.5) ↔ Demo (Sonnet 4.6)

> **Trạng thái hiệu lực (chỉ có tác dụng sau khi hoàn thành Task B6 và B4):** Trong codebase hiện tại mới có `AiGenerationOptions` (B3: bind section `AiGeneration` cho `MaxRepairAttempts`, `MaxOutputTokens`, `Temperature`), chưa có code đọc nhánh `Bedrock/*`. Các lệnh chuyển đổi `Bedrock/ModelId` và `Bedrock/EnableAnswerVerification` dưới đây **chỉ có hiệu lực thực tế sau khi Task B6 (Worker Bedrock adapter bind `Bedrock:Region`, `Bedrock:ModelId`) và Task B4 (bước kiểm chứng đáp án bind `Bedrock:EnableAnswerVerification` hoặc ánh xạ sang `AiGenerationOptions`) được triển khai**.

**Trước buổi demo (chuyển sang Sonnet 4.6 + bật kiểm chứng đáp án):**

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

Trên EC2 (qua SSM Session Manager), lấy secret trực tiếp từ Parameter Store để ghi vào `/etc/exammaker/origin-secret.conf` (không sao chép qua chat; ghi qua file tạm `.tmp` kèm bản sao lưu `.bak` trước khi chạy `nginx -t`, và dùng `if/else` không có `exit` để không làm đóng phiên SSM tương tác):

```bash
SECRET="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Origin/VerifySecret --with-decryption --query Parameter.Value --output text)"
if [ -z "$SECRET" ]; then
  echo "ERROR: Cannot read /exammaker/dev/Origin/VerifySecret from SSM" >&2
else
  sudo install -d -o root -g nginx -m 0750 /etc/exammaker
  sudo install -o root -g nginx -m 0640 /dev/null /etc/exammaker/origin-secret.conf.tmp
  if sudo tee /etc/exammaker/origin-secret.conf.tmp >/dev/null <<EOF
map \$http_x_origin_verify \$origin_header_valid {
    default 0;
    "${SECRET}" 1;
}
EOF
  then
    HAD_PREV=0
    if [ -f /etc/exammaker/origin-secret.conf ]; then
      sudo cp -af /etc/exammaker/origin-secret.conf /etc/exammaker/origin-secret.conf.bak
      HAD_PREV=1
    fi
    if sudo mv -f /etc/exammaker/origin-secret.conf.tmp /etc/exammaker/origin-secret.conf; then
      if sudo nginx -t && sudo systemctl reload nginx; then
        sudo rm -f /etc/exammaker/origin-secret.conf.bak
        echo "SUCCESS: Updated /etc/exammaker/origin-secret.conf and reloaded Nginx."
      elif [ "$HAD_PREV" -eq 1 ] && [ -f /etc/exammaker/origin-secret.conf.bak ]; then
        echo "ERROR: nginx -t or reload failed; restoring previous /etc/exammaker/origin-secret.conf" >&2
        sudo mv -f /etc/exammaker/origin-secret.conf.bak /etc/exammaker/origin-secret.conf
      else
        echo "ERROR: nginx -t or reload failed on initial install; replacing with safe deny-all map placeholder" >&2
        sudo tee /etc/exammaker/origin-secret.conf >/dev/null <<'EOF_EMPTY'
map $http_x_origin_verify $origin_header_valid {
    default 0;
}
EOF_EMPTY
      fi
    else
      sudo rm -f /etc/exammaker/origin-secret.conf.tmp /etc/exammaker/origin-secret.conf.bak
      echo "ERROR: Failed to move /etc/exammaker/origin-secret.conf.tmp into place" >&2
    fi
    unset HAD_PREV
  else
    sudo rm -f /etc/exammaker/origin-secret.conf.tmp
    echo "ERROR: Failed to write temporary /etc/exammaker/origin-secret.conf.tmp; active config untouched." >&2
  fi
fi
unset SECRET
```

> **Quy trình xoay vòng secret không downtime (3 bước — đọc secret đang chạy trực tiếp từ Nginx trên EC2):**
> *Quy tắc bắt buộc:* Phải hoàn tất **trọn vẹn cả 3 bước** (đến khi CloudFront chuyển sang `Deployed` và đã dọn secret cũ ở Bước 3) trước khi bắt đầu một đợt xoay mới.
> 1. **Sinh secret mới (trên máy OPS / CloudShell):**
>    Chạy lệnh sinh secret mới lên SSM Parameter Store (mỗi đợt xoay **chỉ chạy đúng 1 lần**; script sẽ in rõ `previous version <N> -> new version <N+1>` để đối chiếu):
>    ```bash
>    ROTATE_ORIGIN_SECRET=1 ./infra/ssm/setup-ssm.sh
>    ```
> 2. **Trên EC2 (qua SSM Session Manager), trích xuất secret đang dùng từ file cấu hình Nginx hiện tại và nạp kèm secret mới từ SSM:**
>    *Lưu ý quan trọng:* File `/etc/exammaker/origin-secret.conf` có quyền `root:nginx 0640`, còn phiên SSM Session Manager chạy dưới `ssm-user`, nên bắt buộc đọc bằng `sudo`. Nếu file đang có **nhiều hơn 1 secret** (tức đợt xoay trước chưa hoàn tất Bước 3), snippet sẽ dừng và báo lỗi để tránh lấy nhầm secret cũ nhất làm rớt traffic CloudFront. Nội dung mới được ghi qua `.tmp` kèm bản sao lưu `.bak`, không dùng `exit 1`:
>    ```bash
>    SECRET_COUNT="$(sudo grep -cE '^[[:space:]]*"[^"]{32,}"[[:space:]]*1;' /etc/exammaker/origin-secret.conf 2>/dev/null || true)"
>    OLD_SECRET="$(sudo sed -n 's/^[[:space:]]*"\([^"]\{32,\}\)"[[:space:]]*1;.*/\1/p' /etc/exammaker/origin-secret.conf | head -n 1)"
>    NEW_SECRET="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Origin/VerifySecret --with-decryption --query Parameter.Value --output text)"
>
>    if [ -z "$OLD_SECRET" ]; then
>      echo "ERROR: Cannot extract currently active secret from /etc/exammaker/origin-secret.conf" >&2
>    elif [ -z "$NEW_SECRET" ]; then
>      echo "ERROR: Cannot read new secret from SSM" >&2
>    elif [ "${SECRET_COUNT:-0}" -gt 1 ]; then
>      echo "ERROR: /etc/exammaker/origin-secret.conf already contains ${SECRET_COUNT} secrets from an unfinished rotation. Complete Step 3 (wait for CloudFront Deployed and prune old secret) before rotating again." >&2
>    elif [ "$OLD_SECRET" = "$NEW_SECRET" ]; then
>      echo "INFO: Active secret in Nginx already matches latest SSM secret. No dual-secret reload needed."
>    else
>      sudo install -o root -g nginx -m 0640 /dev/null /etc/exammaker/origin-secret.conf.tmp
>      if sudo tee /etc/exammaker/origin-secret.conf.tmp >/dev/null <<EOF
>    map \$http_x_origin_verify \$origin_header_valid {
>        default 0;
>        "${OLD_SECRET}" 1;
>        "${NEW_SECRET}" 1;
>    }
>    EOF
>      then
>        sudo cp -af /etc/exammaker/origin-secret.conf /etc/exammaker/origin-secret.conf.bak
>        if sudo mv -f /etc/exammaker/origin-secret.conf.tmp /etc/exammaker/origin-secret.conf; then
>          if sudo nginx -t && sudo systemctl reload nginx; then
>            sudo rm -f /etc/exammaker/origin-secret.conf.bak
>            echo "SUCCESS: Nginx is now accepting BOTH old and new secrets without downtime."
>          else
>            echo "ERROR: nginx -t or reload failed; restoring previous /etc/exammaker/origin-secret.conf" >&2
>            sudo mv -f /etc/exammaker/origin-secret.conf.bak /etc/exammaker/origin-secret.conf
>          fi
>        else
>          sudo rm -f /etc/exammaker/origin-secret.conf.tmp /etc/exammaker/origin-secret.conf.bak
>          echo "ERROR: Failed to move /etc/exammaker/origin-secret.conf.tmp into place" >&2
>        fi
>      else
>        sudo rm -f /etc/exammaker/origin-secret.conf.tmp
>        echo "ERROR: Failed to write temporary /etc/exammaker/origin-secret.conf.tmp; active config untouched." >&2
>      fi
>    fi
>    unset SECRET_COUNT OLD_SECRET NEW_SECRET
>    ```
> 3. **Cập nhật CloudFront (O5) sang secret mới và dọn secret cũ trên EC2:**
>    - Lấy giá trị mới trên máy OPS (`aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Origin/VerifySecret --with-decryption --query Parameter.Value --output text`) và cập nhật custom header `X-Origin-Verify` trên CloudFront Origin, chờ distribution chuyển sang **`Deployed`**.
>    - Quay lại phiên EC2, chạy lại đoạn script ở đầu mục 4.3 để `/etc/exammaker/origin-secret.conf` chỉ giữ duy nhất secret mới và reload Nginx.
