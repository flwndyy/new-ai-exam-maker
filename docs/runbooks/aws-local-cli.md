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
| OPS (dựng hạ tầng, chạy script `infra/`) | **Có** | Quyền quản trị cho các dịch vụ dự án (giai đoạn dựng có thể dùng `AdministratorAccess`, thu hẹp sau) |
| DEV backend/frontend (chỉ đọc cấu hình) | Không bắt buộc | `AmazonSSMReadOnlyAccess` hoặc policy chỉ đọc `/exammaker/*` |

DEV không có AWS CLI vẫn làm được: OPS đọc tham số và gửi riêng các giá trị **không bí mật**
(Region, UserPoolId, ClientId, Authority, HostedUiDomain) để DEV đặt biến môi trường local.
Backend validate JWT Cognito qua endpoint HTTPS công khai, không cần AWS credentials.

---

## 2. Dành cho người quản trị: tạo IAM User cho từng thành viên

Làm trên AWS Console, **mỗi thành viên một IAM User riêng** (không dùng chung key, không tạo
Access Key cho tài khoản root).

1. AWS Console → **IAM** → **Users** → **Create user**.
2. Đặt tên theo người dùng, ví dụ `ops-<ten>` hoặc `dev-<ten>`. Không cần bật Console access nếu
   người đó chỉ dùng CLI.
3. **Permissions** → **Attach policies directly** → chọn policy theo bảng ở mục 1 → **Create user**.
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
- **Linux (x86_64):**
  ```bash
  curl "https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip" -o "awscliv2.zip"
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
# Tham số Cognito
aws ssm get-parameters-by-path --region ap-southeast-1 \
  --path "/exammaker/dev/Cognito" \
  --query "Parameters[].{Name:Name,Value:Value}" --output table
```

Chạy backend local và để backend tự nạp từ Parameter Store
(`ParameterStoreConfiguration.cs`):

```powershell
# PowerShell
$env:ParameterStore__Path = "/exammaker/dev"
```

```bash
# Bash
export ParameterStore__Path="/exammaker/dev"
```

Không có AWS CLI thì đặt trực tiếp biến môi trường local `Cognito__Region`,
`Cognito__UserPoolId`, `Cognito__ClientId`, `Cognito__Authority` theo giá trị OPS gửi riêng
(xem `docs/runbooks/cognito.md`).

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
