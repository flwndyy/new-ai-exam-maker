# Runbook — Làm việc với AWS từ máy cá nhân (AWS CLI, không dùng CloudShell)

Tài liệu cho toàn bộ thành viên **AI Exam Maker** thao tác với AWS account chung của nhóm trực
tiếp từ máy cá nhân (Windows, macOS, Linux) bằng AWS CLI, thay cho AWS CloudShell.

> **Vì sao không dùng CloudShell?** Tài khoản AWS mới có thể bị chặn CloudShell trong thời gian
> xác minh (thông báo *"Unable to create the environment. Your account verification is in
> progress..."*, có thể kéo dài tới 2 ngày). AWS CLI trên máy cá nhân gọi thẳng AWS API qua
> HTTPS nên không bị ảnh hưởng, và chạy được toàn bộ script trong `infra/`.

> **Quy tắc bảo mật:** Không commit Access Key, Secret Key, account ID, ARN, ID tài nguyên thật
> (`UserPoolId`, `ClientId`...) vào repository. Không dán key vào nhóm chat chung. Xem
> `AGENTS.md` và `CONTRIBUTING.md`.

---

## 1. Ai cần làm gì?

| Vai trò | Có cần AWS CLI? | Quyền IAM gợi ý |
|---|---|---|
| OPS (dựng hạ tầng, chạy script `infra/`) | **Có** | Policy giới hạn theo dịch vụ dự án tại region `ap-southeast-1` (EC2, RDS, S3, CloudFront, Cognito, SSM, Route53, ACM, Bedrock). Không cấp `AdministratorAccess` cho access key dài hạn trên laptop; xoay key định kỳ ≤ 90 ngày |
| DEV backend/frontend (chỉ đọc cấu hình) | Không bắt buộc | Policy tối thiểu chỉ đọc SSM Cognito dev: `/exammaker/dev/Cognito/*` (tuyệt đối **không** dùng `AmazonSSMReadOnlyAccess` hay wildcard `/exammaker/*` để tránh lộ connection string/secret) |

DEV không có AWS CLI vẫn làm được: OPS đọc tham số và gửi riêng các giá trị **không bí mật**
(Region, UserPoolId, ClientId, HostedUiDomain) để DEV đặt biến môi trường local.
Backend validate JWT Cognito qua endpoint HTTPS công khai, không cần AWS credentials hay biến Authority (API tự tính issuer theo `docs/a3-auth.md`).

---

## 2. Dành cho người quản trị: tạo IAM User cho từng thành viên

Làm trên AWS Console, **mỗi thành viên một IAM User riêng** (không dùng chung key, không tạo
Access Key cho tài khoản root).

### 2.1 Tạo policy tối thiểu cho DEV (Chỉ đọc tham số Cognito)

> ⚠️ **Cảnh báo bảo mật (Least Privilege):** Tuyệt đối **không** gán AWS Managed Policy `AmazonSSMReadOnlyAccess` hoặc cấp quyền đọc toàn bộ `/exammaker/*` cho DEV.
> `AmazonSSMReadOnlyAccess` cho phép đọc toàn bộ Parameter Store trong account. Khi dùng KMS key mặc định `aws/ssm`, IAM User có thể đọc được cả giá trị `SecureString` nhạy cảm như connection string database (RDS), origin secret CloudFront, hoặc API keys.

1. AWS Console → **IAM** → **Policies** → **Create policy**.
2. Chọn tab **JSON** và dán policy sau:
   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Sid": "AllowReadCognitoParametersOnly",
         "Effect": "Allow",
         "Action": [
           "ssm:GetParameter",
           "ssm:GetParameters",
           "ssm:GetParametersByPath"
         ],
         "Resource": [
           "arn:aws:ssm:ap-southeast-1:<ACCOUNT_ID>:parameter/exammaker/dev/Cognito",
           "arn:aws:ssm:ap-southeast-1:<ACCOUNT_ID>:parameter/exammaker/dev/Cognito/*"
         ]
       }
     ]
   }
   ```
3. Thay `<ACCOUNT_ID>` bằng account ID thật của nhóm (hỏi OPS, **không** commit vào repo).
   *(Lưu ý: Policy giới hạn chính xác môi trường `dev`. Không dùng wildcard `*` cho tên môi trường để tránh cấp nhầm quyền đọc sang `prod` hoặc các subpath khác).*
4. Đặt tên policy: `ExamMakerDevCognitoReadOnly` → **Create policy**. Policy này chỉ cho phép đọc các tham số public của Cognito trên môi trường `dev`, hoàn toàn không cho đọc connection string hay giải mã secrets.

### 2.2 Tạo IAM User và cấp Access Key

1. AWS Console → **IAM** → **Users** → **Create user**.
2. Đặt tên theo người dùng, ví dụ `ops-<ten>` hoặc `dev-<ten>`. Không cần bật Console access nếu
   người đó chỉ dùng CLI.
3. **Permissions** → **Attach policies directly**:
   - Nếu là DEV: chọn policy `ExamMakerDevCognitoReadOnly` vừa tạo ở mục 2.1.
   - Nếu là OPS: chọn Project Infrastructure Policy (giới hạn theo dịch vụ dự án và region `ap-southeast-1`). **Không cấp `AdministratorAccess` cho access key dài hạn lưu trên máy cá nhân** (tuân thủ `docs/context.md` §10, §15). Nếu cần thao tác quản trị đặc biệt một lần, thực hiện trực tiếp trên AWS Console bằng tài khoản có bật MFA.
   → **Create user**.
4. Mở user vừa tạo → tab **Security credentials** → mục **Access keys** → **Create access key**.
5. Use case: **Command Line Interface (CLI)** → tick xác nhận → **Next** → **Create access key**.
6. Gửi **Access key ID** và **Secret access key** cho đúng người đó qua kênh riêng. Secret chỉ
   hiện một lần; nếu mất thì xoá key và tạo key mới.

Khi thành viên rời nhóm hoặc nghi lộ key: **Deactivate/Delete** Access Key của user đó ngay trong
IAM. Xoá commit là không đủ.

---

## 3. Dành cho thành viên: cài đặt trên máy cá nhân

### Bước 1 — Cài AWS CLI v2

- **Windows (PowerShell):**
  ```powershell
  winget install Amazon.AWSCLI
  ```
- **macOS:**
  ```bash
  curl "https://awscli.amazonaws.com/AWSCLIV2.pkg" -o "AWSCLIV2.pkg"
  sudo installer -pkg AWSCLIV2.pkg -target /
  ```
- **Linux (x86_64 / aarch64):**
  ```bash
  # Với kiến trúc x86_64 (Intel / AMD 64-bit):
  curl "https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip" -o "awscliv2.zip"

  # Hoặc với kiến trúc ARM64 / aarch64 (Apple Silicon VM, AWS Graviton, Raspberry Pi...):
  # curl "https://awscli.amazonaws.com/awscli-exe-linux-aarch64.zip" -o "awscliv2.zip"

  unzip awscliv2.zip && sudo ./aws/install
  ```

Mở lại terminal rồi kiểm tra: `aws --version`.

### Bước 2 — Cấu hình credentials

```bash
aws configure
```

Nhập lần lượt:

| Câu hỏi | Giá trị |
|---|---|
| `AWS Access Key ID` | Access key được cấp |
| `AWS Secret Access Key` | Secret key được cấp |
| `Default region name` | `ap-southeast-1` |
| `Default output format` | `json` |

Credentials được lưu cục bộ ở `~/.aws/credentials` (Windows: `%USERPROFILE%\.aws\credentials`).
Không copy file này vào repository.

### Bước 3 — Kiểm tra đúng account của nhóm

```bash
aws sts get-caller-identity
```

Trường `Account` phải đúng **account ID của nhóm** (hỏi OPS, không ghi vào repo) và `Arn` phải
là IAM User của bạn. Nếu ra account khác, bạn đang dùng key/profile cũ: chạy lại `aws configure`
hoặc dùng profile riêng (mục 6).

---

## 4. Dành cho DEV: đọc cấu hình

Mọi tài nguyên dự án nằm ở region **`ap-southeast-1` (Singapore)**. Trên AWS Console, nhớ đổi
region ở góc phải; để `us-east-1` sẽ không thấy gì.

```bash
# Tham số Cognito (trên Git Bash Windows: thêm MSYS_NO_PATHCONV=1 trước lệnh)
MSYS_NO_PATHCONV=1 aws ssm get-parameters-by-path --region ap-southeast-1 \
  --path "/exammaker/dev/Cognito" \
  --query "Parameters[].{Name:Name,Value:Value}" --output table
```

### Cách 1: Nạp tự động từ Parameter Store (Khi máy đã cấu hình AWS CLI)

`AddSystemsManager(path)` cắt prefix `path` khỏi tên parameter rồi ánh xạ phần còn lại thành .NET configuration key. Ví dụ: `path=/exammaker/dev` → parameter `/exammaker/dev/Cognito/Region` → config key `Cognito:Region`.

Nếu đặt `path=/exammaker/dev/Cognito`, phần `Cognito` bị cắt mất → config key chỉ còn `Region`, `UserPoolId`... và backend không tìm thấy section `Cognito:*` → auth sẽ fail.

**Vì vậy path đúng là `/exammaker/<env>` (khớp với `docs/runbooks/cognito.md` §4).**

> ⚠️ **Lưu ý về quyền IAM:** DEV có policy `ExamMakerDevCognitoReadOnly` chỉ cho phép đọc `/exammaker/dev/Cognito/*`, trong khi `AddSystemsManager("/exammaker/dev")` gọi `GetParametersByPath` trên toàn bộ `/exammaker/dev` (sẽ nhận `AccessDeniedException` cho các path ngoài Cognito). Do đó, **DEV chạy local bắt buộc dùng Cách 2** (đặt biến môi trường trực tiếp, để trống `ParameterStore__Path`). Cách 1 chỉ áp dụng cho OPS hoặc EC2 Instance Profile (`O3`) có quyền đọc toàn bộ prefix dự án.

```powershell
# PowerShell (dành cho OPS hoặc tài khoản có quyền đọc /exammaker/dev/*)
$env:ParameterStore__Path = "/exammaker/dev"
dotnet run --project backend/src/ExamMaker.Api --urls http://localhost:5080
```

```bash
# Bash (dành cho OPS hoặc tài khoản có quyền đọc /exammaker/dev/*)
export ParameterStore__Path="/exammaker/dev"
dotnet run --project backend/src/ExamMaker.Api --urls http://localhost:5080
```

### Cách 2: Chạy local HOÀN TOÀN KHÔNG CẦN AWS CLI (Dành cho DEV không cài CLI)

Thành viên không cài AWS CLI vẫn làm việc và test bình thường. OPS sẽ lấy các giá trị từ Parameter Store và gửi riêng qua tin nhắn (đây là các thông số public/non-secret):

#### A. Dành cho Backend (.NET 10 — Task A3, A4)

Đặt trực tiếp biến môi trường trong terminal trước khi chạy (hoặc thêm vào mục `environmentVariables` trong `launchSettings.json` local):

```powershell
# PowerShell (Windows)
$env:Cognito__Region = "ap-southeast-1"
$env:Cognito__UserPoolId = "<UserPoolId-do-OPS-cung-cap>"
$env:Cognito__ClientId = "<ClientId-do-OPS-cung-cap>"
dotnet run --project backend/src/ExamMaker.Api --urls http://localhost:5080
```

```bash
# Bash (macOS / Linux / Git Bash)
export Cognito__Region="ap-southeast-1"
export Cognito__UserPoolId="<UserPoolId-do-OPS-cung-cap>"
export Cognito__ClientId="<ClientId-do-OPS-cung-cap>"
dotnet run --project backend/src/ExamMaker.Api --urls http://localhost:5080
```

*(Theo `docs/a3-auth.md`, API tự tính OIDC issuer từ `Region` và `UserPoolId` (`https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}`), nên **không cần đặt biến `Cognito__Authority`**. API tự động tải JWKS public key qua endpoint OIDC công khai của AWS để xác thực chữ ký RS256 của access token — **không cần tài khoản AWS hay credentials**. Khi gọi endpoint được bảo vệ như `GET /api/v1/me`, DEV cần đặt thêm `ConnectionStrings__ExamMaker` kết nối PostgreSQL local).*

#### B. Dành cho Frontend (Next.js — Task B2)

Tạo file `frontend/.env.local` (file này đã có trong `.gitignore`, không commit lên repo):

```env
NEXT_PUBLIC_COGNITO_DOMAIN=<HostedUiDomain-do-OPS-cung-cap>
NEXT_PUBLIC_COGNITO_CLIENT_ID=<ClientId-do-OPS-cung-cap>
NEXT_PUBLIC_COGNITO_REDIRECT_URI=http://localhost:3000/auth/callback/
NEXT_PUBLIC_COGNITO_LOGOUT_URI=http://localhost:3000/
```

---

## 5. Dành cho OPS: chạy script `infra/`

Các script trong `infra/` là Bash, cần thêm `jq`:

- Windows: `winget install jqlang.jq` (mở lại terminal sau khi cài)
- macOS: `brew install jq`
- Linux: `sudo apt install jq`

**Windows:** mở **Git Bash** (không chạy từ PowerShell, vì cú pháp xuống dòng `\` của Bash không
dùng được trong PowerShell). Git Bash đọc cùng file credentials của `aws configure`. Các script đã
đặt sẵn `MSYS_NO_PATHCONV=1` để Git Bash không đổi đường dẫn SSM `/exammaker/...`; khi gõ lệnh
`aws ssm ...` bằng tay trong Git Bash, thêm `MSYS_NO_PATHCONV=1` ở đầu lệnh.

```bash
# Từ thư mục gốc repository
bash infra/cognito/setup-cognito.sh     # lần đầu cần COGNITO_DOMAIN_PREFIX=...
bash infra/cognito/verify-cognito.sh
```

Chi tiết từng hạ tầng xem runbook tương ứng trong `docs/runbooks/`.

---

## 6. Nhiều account trên một máy (tuỳ chọn)

Nếu máy đã có credentials của account cá nhân khác, dùng profile riêng để tránh chạy nhầm:

```bash
aws configure --profile exammaker
aws sts get-caller-identity --profile exammaker
```

Trước khi chạy script, chọn profile cho phiên terminal:

```bash
export AWS_PROFILE=exammaker        # Bash / Git Bash
```

```powershell
$env:AWS_PROFILE = "exammaker"      # PowerShell
```

Luôn chạy `aws sts get-caller-identity` trước khi tạo hoặc xoá tài nguyên.

---

## 7. Xử lý sự cố

| Triệu chứng | Nguyên nhân | Cách xử lý |
|---|---|---|
| `Unable to locate credentials` / `NoCredentials` | Chưa chạy `aws configure` hoặc sai profile | Chạy `aws configure`, kiểm tra `AWS_PROFILE` |
| `The security token included in the request is invalid` | Key sai, đã bị xoá, hoặc còn biến môi trường `AWS_ACCESS_KEY_ID` cũ | Xoá biến môi trường cũ, nhập lại key đúng |
| `get-caller-identity` ra account khác | Dùng key/profile của account cá nhân | Dùng profile riêng (mục 6) |
| Lệnh SSM trả rỗng | Sai region, sai account, hoặc tham số chưa được tạo | Kiểm tra `--region ap-southeast-1` và account ID |
| `AccessDenied` | IAM User thiếu quyền | Báo OPS cấp đúng policy |
| Git Bash: SSM path thành `C:/Program Files/Git/exammaker/...` | MSYS đổi đường dẫn | Thêm `MSYS_NO_PATHCONV=1` trước lệnh |
| `jq: command not found` | Chưa cài `jq` hoặc chưa mở lại terminal | Cài `jq` rồi mở lại Git Bash |
