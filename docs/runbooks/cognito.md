# Runbook — Triển khai Amazon Cognito User Pool (Task O9)

Tài liệu hướng dẫn dựng, kiểm tra, bootstrap admin đầu tiên, bàn giao và xử lý sự cố cho **Amazon Cognito User Pool** của dự án **AI Exam Maker** theo đúng kiến trúc đã chốt tại `docs/context.md` (mục 3.3, 5.2, 5.4, 9.2, 14).

> **Quy tắc bảo mật:** Tuyệt đối **không** commit `UserPoolId`, `ClientId`, AWS Account ID, ARN thật hay thông tin user thật vào repository, và **không** gửi ID thật qua nhóm chat công khai. Mọi thông số được bàn giao cho DEV-A (`A4`) và DEV-B (`B2`) thông qua **AWS Systems Manager Parameter Store**.

---

## 1. Thông số kiến trúc chuẩn

| Thành phần | Giá trị chuẩn | Tham chiếu |
|---|---|---|
| Region | `ap-southeast-1` (Singapore) | `docs/context.md` §14 |
| Gói Cognito (Tier) | `ESSENTIALS` (hỗ trợ Managed Login v2, **Always Free ≤ 10.000 MAU**) | `docs/context.md` §6 |
| Bảo vệ xoá (DeletionProtection) | `dev`: `INACTIVE` · `prod`: `ACTIVE` (giữ nguyên khi chạy lại script, trừ khi truyền `DELETION_PROTECTION`) | `infra/cognito/setup-cognito.sh` |
| Tự đăng ký (Self sign-up) | **Tắt** (`AllowAdminCreateUserOnly = true`) | `docs/context.md` §3.3 |
| Định danh đăng nhập | `email` (không phân biệt hoa/thường), tự xác minh email | `docs/context.md` §3.3 |
| 3 Nhóm người dùng (Groups) | `admin` (precedence 1), `head` (2), `teacher` (3) — chữ thường | `docs/context.md` §3, `openapi.yaml` |
| App Client | Public SPA (**không tạo Client Secret**), chỉ bật OAuth flow `code` (**PKCE**) | `docs/context.md` §5.4, §9.2 |
| Explicit Auth Flows | Chỉ bật `ALLOW_REFRESH_TOKEN_AUTH` (**tắt** `ALLOW_USER_SRP_AUTH` và `ALLOW_USER_PASSWORD_AUTH`) | `docs/context.md` §3.3, §9.2 |
| OAuth Scopes | `openid`, `email`, `profile` | `docs/context.md` §9.2 |
| Token TTL | Access token: **60 phút** · ID token: **60 phút** · Refresh token: **7 ngày** | `docs/context.md` §3.3 |
| Callback URL (`trailingSlash: true`) | Local: `http://localhost:3000/auth/callback/` · Prod: `https://app.<domain>/auth/callback/` | `frontend/next.config.ts` |
| Logout URL | Local: `http://localhost:3000/` · Prod: `https://app.<domain>/` | `docs/context.md` §9.2 |
| Quên mật khẩu & đổi mật khẩu lần đầu | Managed Login v2 + khôi phục qua `verified_email` (`COGNITO_DEFAULT`) | `docs/context.md` §3.3 |
| Bảo mật bổ sung | `PreventUserExistenceErrors = ENABLED`, `EnableTokenRevocation = true` | `docs/context.md` §10 |

---

## 2. Cách 1: Dựng tự động bằng AWS CLI script (Khuyên dùng)

**Yêu cầu công cụ:** `aws` (CLI v2), `jq`, `curl` — cả 3 đều **được cài sẵn trong AWS CloudShell** (trong AWS Console, chọn region **`ap-southeast-1`**). Script cũng chạy được trên Linux, macOS, hoặc Git Bash trên Windows (đã tích hợp `MSYS_NO_PATHCONV=1` chống đổi đường dẫn `/exammaker/...`; nếu thiếu `jq` trên Windows, cài bằng `winget install jqlang.jq`).

**Hành vi an toàn khi chạy lại (idempotent):**
- **User Pool đã tồn tại:** script đọc cấu hình hiện tại qua `describe-user-pool`. Nếu các thiết lập bắt buộc đã đúng, script **bỏ qua `update-user-pool`**; nếu có lệch, script trộn (merge) vào cấu hình hiện có và **không ghi đè** `DeletionProtection`, MFA, mẫu email, `UserAttributeUpdateSettings`, hay Lambda trigger đã cấu hình tay.
- **App Client đã tồn tại:** script **gộp (merge)** danh sách `CallbackURLs`/`LogoutURLs` mới vào danh sách đang có (không làm mất domain thật khi chạy lại lệnh dev), đồng thời giữ nguyên các cấu hình không quản lý (`ReadAttributes`, `WriteAttributes`, `AuthSessionValidity`...). Nếu cần thay thế hoàn toàn danh sách URL cũ, truyền thêm `REPLACE_URLS=true`. Ở môi trường `prod` (`INCLUDE_LOCALHOST=false`), mọi URL `localhost`/`127.0.0.1` sẽ tự động bị loại bỏ.
- **Phát hiện client lỗi sớm:** nếu App Client trùng tên đã có `ClientSecret`, script báo lỗi ngay vì Cognito không cho phép gỡ secret bằng `update-user-pool-client`.

### Bước 1: Dựng cho môi trường `dev` (mở khóa ngay cho DEV-A và DEV-B)

Chọn một tiền tố domain Cognito duy nhất toàn vùng `ap-southeast-1` (chữ thường, số, dấu `-`, không chứa chữ `aws`/`amazon`/`cognito`):

```bash
COGNITO_DOMAIN_PREFIX="exammaker-dev-<ten-duy-nhat>" \
  ./infra/cognito/setup-cognito.sh
```

### Bước 2: Cập nhật thêm domain thật (sau khi OPS-1 hoàn tất task `O4`)

```bash
# Môi trường dev (gộp thêm https://app.<domain>/auth/callback/, giữ lại localhost)
APP_DOMAIN="<domain-cua-nhom>" \
  ./infra/cognito/setup-cognito.sh

# Môi trường prod (chỉ giữ https://app.<domain>/auth/callback/, loại bỏ localhost, bật DeletionProtection=ACTIVE)
ENV_NAME=prod INCLUDE_LOCALHOST=false APP_DOMAIN="<domain-cua-nhom>" \
  COGNITO_DOMAIN_PREFIX="exammaker-prod-<ten-duy-nhat>" \
  ./infra/cognito/setup-cognito.sh
```

---

## 3. Cách 2: Dựng thủ công trên AWS Console

Nếu dựng trực tiếp trên AWS Management Console tại region **`ap-southeast-1`**:

1. **Tạo User Pool** (*Amazon Cognito → User pools → Create user pool*):
   - Tier: **Essentials** (miễn phí ≤ 10.000 MAU).
   - Application type: **Single-page application (SPA)**, tên client `exammaker-dev-spa`.
   - Sign-in identifiers: chọn **Email**.
   - Self-registration: **Bỏ chọn / Tắt** (*Enable self-registration = OFF*).
   - Required attributes: `email`; thêm mutable attribute `name`.
   - Return URL (Callback URL): `http://localhost:3000/auth/callback/`.
   - Với môi trường `prod`, bật **Deletion protection = ACTIVE**.
2. **Kiểm tra cấu hình bảo mật & mật khẩu** (*Authentication → Sign-in / Password policies*):
   - Password policy: tối thiểu **10 ký tự**, yêu cầu chữ hoa, chữ thường, chữ số; mật khẩu tạm hết hạn sau **7 ngày**.
   - Self-service account recovery: **Bật**, chỉ chọn **Email only** (`verified_email`).
   - Email provider: **Send email with Cognito** (`COGNITO_DEFAULT`).
3. **Tạo 3 Groups** (*User management → Groups → Create group*):
   - `admin` (Precedence `1`)
   - `head` (Precedence `2`)
   - `teacher` (Precedence `3`)
4. **Cấu hình Managed Login Domain** (*Branding → Domain*):
   - Tạo Cognito domain prefix duy nhất (Managed Login v2), đảm bảo Managed Login Branding đã được gắn cho App Client.
5. **Kiểm tra App Client** (*Applications → App clients → `exammaker-dev-spa`*):
   - **Không có Client Secret**.
   - **Authentication flows**: chỉ bật `ALLOW_REFRESH_TOKEN_AUTH` (**tắt** `ALLOW_USER_SRP_AUTH` và `ALLOW_USER_PASSWORD_AUTH`).
   - **Login pages**: Callback URL `http://localhost:3000/auth/callback/` (và `https://app.<domain>/auth/callback/` khi có domain; môi trường `prod` không chứa `localhost`); Sign-out URL `http://localhost:3000/` (hoặc `https://app.<domain>/`); Identity provider `Cognito user pool`; OAuth grant types **chỉ chọn `Authorization code grant`**; OpenID Connect scopes: `openid`, `email`, `profile`.
   - **Token validity**: Access token **60 phút**, ID token **60 phút**, Refresh token **7 ngày**.
   - Bật **Prevent user existence errors** (`ENABLED`) và **Token revocation**.
6. **Ghi thông số lên SSM Parameter Store** (*Systems Manager → Parameter Store → Create parameter*, Type = `String`, Tier = `Standard`):
   - `/exammaker/<env>/Cognito/Region` = `ap-southeast-1`
   - `/exammaker/<env>/Cognito/UserPoolId` = `<UserPoolId>`
   - `/exammaker/<env>/Cognito/ClientId` = `<ClientId>`
   - `/exammaker/<env>/Cognito/Authority` = `https://cognito-idp.ap-southeast-1.amazonaws.com/<UserPoolId>`
   - `/exammaker/<env>/Cognito/HostedUiDomain` = `https://<prefix>.auth.ap-southeast-1.amazoncognito.com`

---

## 4. Quy ước bàn giao cho DEV-A (`A4`) và DEV-B (`B2`)

Cấu trúc key SSM dưới `/exammaker/<env>/Cognito/*` tương thích trực tiếp với `backend/src/ExamMaker.Infrastructure/ParameterStoreConfiguration.cs` (khi `ParameterStore__Path=/exammaker/<env>`).

### Đọc cấu hình từ Parameter Store (dành cho thành viên có quyền IAM đọc SSM)

```bash
MSYS_NO_PATHCONV=1 aws ssm get-parameters-by-path \
  --region ap-southeast-1 \
  --path "/exammaker/dev/Cognito" \
  --query "Parameters[].{Name:Name,Value:Value}" \
  --output table
```

### Ánh xạ sang cấu hình ứng dụng

- **Backend (.NET 10 — `A3`, `A4`):**
  - Chạy trên EC2 (tự nạp từ SSM khi đặt `ParameterStore__Path=/exammaker/<env>`): đọc qua section `Cognito` (`Cognito:Region`, `Cognito:UserPoolId`, `Cognito:ClientId`). *(Lưu ý: tham số `Cognito:Authority` trong SSM dùng làm giá trị tham chiếu OIDC; theo `docs/a3-auth.md`, API tự tính issuer từ Region và UserPoolId nên không cần đọc trực tiếp `Cognito:Authority`).*
  - Chạy local không cần SSM: đặt 3 biến môi trường `Cognito__Region`, `Cognito__UserPoolId`, `Cognito__ClientId` (không commit vào repo; API tự tính issuer từ Region + UserPoolId).
  - **Quyền IAM tối thiểu cho EC2 Instance Profile (tác vụ `O3`):** giới hạn `Resource` chỉ tới ARN của User Pool này, bao gồm đúng các action ứng dụng thực sự gọi theo `docs/context.md` §3.3 và `docs/api/openapi.yaml`:
    - `cognito-idp:AdminCreateUser` — tạo user mới (`POST /admin/users`), gửi mật khẩu tạm qua email.
    - `cognito-idp:AdminDeleteUser` — **bắt buộc cho cơ chế rollback/đền bù** khi `AdminAddUserToGroup` hoặc ghi bảng `users` lỗi, tránh để lại user mồ côi trên Cognito.
    - `cognito-idp:AdminGetUser` — kiểm tra trạng thái/thuộc tính user khi cập nhật hoặc đối soát (`POST /admin/users/reconcile`).
    - `cognito-idp:AdminAddUserToGroup`, `cognito-idp:AdminRemoveUserFromGroup`, `cognito-idp:AdminListGroupsForUser` — đồng bộ role với 3 group `admin`/`head`/`teacher`.
    - `cognito-idp:AdminDisableUser`, `cognito-idp:AdminEnableUser`, `cognito-idp:AdminUserGlobalSignOut` — vô hiệu hóa/kích hoạt lại user và thu hồi phiên.
    - `cognito-idp:AdminUpdateUserAttributes` — đồng bộ khi đổi `email` hoặc `full_name`.
    - `cognito-idp:ListUsers` — đối soát toàn bộ user giữa Cognito và PostgreSQL (`POST /admin/users/reconcile`).
    - *(Chủ đích **không** cấp `cognito-idp:AdminSetUserPassword` cho EC2 Instance Profile: theo `docs/api/openapi.yaml`, `POST /admin/users` không nhận hay đặt mật khẩu — Cognito tự sinh mật khẩu tạm gửi email mời và người dùng tự đổi/quên mật khẩu qua Managed Login. Bỏ action này giúp giữ nguyên tắc least-privilege trên EC2).*
- **Frontend (Next.js static export — `B2`):**
  - Biến môi trường build-time trong `.env.local` (chỉ nằm ở local hoặc GitHub Actions Variables khi deploy):
    - `NEXT_PUBLIC_COGNITO_DOMAIN` = giá trị của `/exammaker/<env>/Cognito/HostedUiDomain`
    - `NEXT_PUBLIC_COGNITO_CLIENT_ID` = giá trị của `/exammaker/<env>/Cognito/ClientId`
    - `NEXT_PUBLIC_COGNITO_REDIRECT_URI` = `http://localhost:3000/auth/callback/` (hoặc `https://app.<domain>/auth/callback/`)
    - `NEXT_PUBLIC_COGNITO_LOGOUT_URI` = `http://localhost:3000/` (hoặc `https://app.<domain>/`)

---

## 5. Quy trình tạo Admin đầu tiên (Bootstrap Admin)

Theo `docs/context.md` §3.3, vì self sign-up đã bị tắt và API từ chối mọi `sub` chưa có trong bảng `users`, tài khoản **Admin đầu tiên** được người triển khai tạo **một lần duy nhất** bằng CLI/script (không qua API công khai).

> **Lưu ý phối hợp với task `A4`:** Khi DEV-A hoàn tất migration `A2` và script bootstrap ở task `A4`, có thể dùng trực tiếp script của `A4` cho Bước 2. Quy trình bên dưới mô tả đầy đủ hai phía Cognito + PostgreSQL theo `docs/erd.md`.

### Bước 1: Tạo user trên Cognito và gán vào nhóm `admin`

Chạy trong AWS CloudShell (`ap-southeast-1`) bằng quyền IAM của người triển khai:

```bash
ENV_NAME="${ENV_NAME:-dev}"
ADMIN_EMAIL="<admin-email>"
ADMIN_NAME="<Ho va ten Admin>"

USER_POOL_ID="$(aws ssm get-parameter --region ap-southeast-1 \
  --name "/exammaker/${ENV_NAME}/Cognito/UserPoolId" \
  --query "Parameter.Value" --output text)"

COGNITO_SUB="$(aws cognito-idp admin-create-user \
  --region ap-southeast-1 \
  --user-pool-id "$USER_POOL_ID" \
  --username "$ADMIN_EMAIL" \
  --desired-delivery-mediums EMAIL \
  --user-attributes Name=email,Value="$ADMIN_EMAIL" Name=email_verified,Value=true Name=name,Value="$ADMIN_NAME" \
  --query "User.Attributes[?Name=='sub'].Value | [0]" \
  --output text)"

aws cognito-idp admin-add-user-to-group \
  --region ap-southeast-1 \
  --user-pool-id "$USER_POOL_ID" \
  --username "$ADMIN_EMAIL" \
  --group-name admin

echo "Created Cognito admin user. cognito_sub=${COGNITO_SUB}"
```

*(Cognito sẽ gửi mật khẩu tạm về `$ADMIN_EMAIL`; nếu bước gán group thất bại, xóa user vừa tạo bằng `aws cognito-idp admin-delete-user` trước khi thử lại).*

### Bước 2: Chèn dòng tương ứng vào bảng `users` trong PostgreSQL

Sau khi RDS (`O10`) và migration (`A2`) đã sẵn sàng, kết nối DB qua SSM Session Manager trên EC2 (hoặc chạy script bootstrap của DEV-A ở task `A4`) và chèn đúng `cognito_sub` lấy ở Bước 1 (`department_id` để `NULL` cho `admin`, `role = 'admin'`, `is_active = true` theo `docs/erd.md`):

```sql
INSERT INTO users (id, cognito_sub, email, full_name, role, department_id, is_active)
VALUES (gen_random_uuid(), '<COGNITO_SUB>', '<admin-email>', '<Ho va ten Admin>', 'admin', NULL, true)
ON CONFLICT (cognito_sub) DO NOTHING;
```

*(Kiểm tra lại tên cột chính xác theo migration `A2` trước khi chạy nếu có thay đổi schema).*

### Bước 3: Xác minh đăng nhập & đổi mật khẩu lần đầu

1. Mở ứng dụng web → chuyển sang Managed Login → đăng nhập bằng `$ADMIN_EMAIL` và mật khẩu tạm nhận qua email.
2. Hoàn tất bước bắt buộc **đổi mật khẩu lần đầu** (`NEW_PASSWORD_REQUIRED`).
3. Xác nhận `GET /api/v1/me` trả về HTTP 200 với `role: "admin"`, `isActive: true`.

---

## 6. Quy trình kiểm thử (Verification)

### 6.1 Kiểm tra tự động bằng script (22 tiêu chí)

```bash
# Kiểm tra môi trường dev (cho phép localhost trong callback/logout)
./infra/cognito/verify-cognito.sh

# Kiểm tra môi trường prod (bắt buộc KHÔNG chứa localhost/127.0.0.1)
ENV_NAME=prod ./infra/cognito/verify-cognito.sh
```

Script gọi tối thiểu số lệnh AWS API (đọc SSM 1 lần, `describe-user-pool` 1 lần, `describe-user-pool-client` 1 lần) và xác minh đủ **22 tiêu chí**:
1. Đủ cả 5 tham số SSM dưới `/exammaker/<env>/Cognito/`, `Region` khớp `AWS_REGION` và `Authority` khớp đúng `https://cognito-idp.<region>.amazonaws.com/<UserPoolId>`.
2. `UserPoolTier = ESSENTIALS` (chống bật nhầm gói `PLUS` gây phát sinh phí).
3. `AllowAdminCreateUserOnly = true` (tắt self sign-up).
4. `AccountRecoverySetting` chỉ dùng `verified_email`.
5. Managed Login domain ở trạng thái `ACTIVE` với `ManagedLoginVersion = 2` và khớp đúng tham số SSM `HostedUiDomain`.
6. Managed Login branding đã tồn tại cho App Client.
7–9. Đủ 3 Cognito group: `admin`, `head`, `teacher`.
10. App Client **không có** `ClientSecret`.
11. `AllowedOAuthFlowsUserPoolClient = true`.
12. `AllowedOAuthFlows` chỉ chứa `code` (Authorization Code + PKCE).
13. `AllowedOAuthScopes` đúng tập `email`, `openid`, `profile`.
14. `ExplicitAuthFlows` chỉ chứa `ALLOW_REFRESH_TOKEN_AUTH` (không bật `ALLOW_USER_SRP_AUTH` / `ALLOW_USER_PASSWORD_AUTH`).
15. Access token TTL = `60 minutes`.
16. ID token TTL = `60 minutes`.
17. Refresh token TTL = `7 days`.
18. `PreventUserExistenceErrors = ENABLED`.
19. `EnableTokenRevocation = true`.
20. `CallbackURLs` không rỗng, mọi URL kết thúc bằng `/auth/callback/`, và **không chứa** `localhost`/`127.0.0.1` khi `ENV_NAME=prod` (`ALLOW_LOCALHOST=false`).
21. `LogoutURLs` không rỗng, và **không chứa** `localhost`/`127.0.0.1` khi `ENV_NAME=prod` (`ALLOW_LOCALHOST=false`).
22. OIDC discovery endpoint (`${AUTHORITY}/.well-known/openid-configuration`) trả HTTP `200`.

### 6.2 Kiểm tra thủ công luồng mật khẩu tạm & quên mật khẩu

1. Tạo một tài khoản kiểm thử `teacher` bằng quyền admin (không commit email thật vào repo):
   ```bash
   USER_POOL_ID="$(MSYS_NO_PATHCONV=1 aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Cognito/UserPoolId --query Parameter.Value --output text)"
   aws cognito-idp admin-create-user \
     --region ap-southeast-1 \
     --user-pool-id "$USER_POOL_ID" \
     --username "<test-email>" \
     --desired-delivery-mediums EMAIL \
     --user-attributes Name=email,Value="<test-email>" Name=email_verified,Value=true Name=name,Value="Test Teacher"
   aws cognito-idp admin-add-user-to-group \
     --region ap-southeast-1 \
     --user-pool-id "$USER_POOL_ID" \
     --username "<test-email>" \
     --group-name teacher
   ```
2. Mở URL đăng nhập Managed Login với PKCE (`response_type=code`, `scope=openid+email+profile`, `redirect_uri=http://localhost:3000/auth/callback/`):
   - Xác nhận **không có** liên kết đăng ký tài khoản mới (*Create an account / Sign up*).
   - Đăng nhập bằng mật khẩu tạm nhận qua email → hệ thống bắt buộc **đổi mật khẩu lần đầu** (`NEW_PASSWORD_REQUIRED`).
   - Sau khi đổi mật khẩu, xác nhận redirect về `http://localhost:3000/auth/callback/?code=...`.
3. Kiểm tra luồng **Quên mật khẩu** (*Forgot your password?*): nhập email test → nhận mã xác thực qua email → đặt mật khẩu mới thành công.
4. **Dọn dẹp user test** sau khi kiểm thử xong:
   ```bash
   aws cognito-idp admin-delete-user \
     --region ap-southeast-1 \
     --user-pool-id "$USER_POOL_ID" \
     --username "<test-email>"
   ```

---

## 7. Xử lý sự cố thường gặp (Troubleshooting)

| Triệu chứng / Lỗi | Nguyên nhân phổ biến | Cách khắc phục |
|---|---|---|
| `UnrecognizedClientException: The security token included in the request is invalid` trên CloudShell | Còn file `~/.aws/credentials` cũ từ lab trước ghi đè token CloudShell, hoặc phiên Console hết hạn. | Chạy `mv ~/.aws/credentials ~/.aws/credentials.bak 2>/dev/null \|\| true` và `unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN`, hoặc bấm **Actions → Restart AWS CloudShell**. |
| Script chạy nhầm region `us-east-1` trong CloudShell | CloudShell tự export `AWS_REGION` theo region đang mở trên Console. | Chuyển AWS Console sang **Singapore (`ap-southeast-1`)** trước khi mở CloudShell, hoặc truyền rõ `AWS_REGION=ap-southeast-1`. |
| `InvalidParameterException: Domain already associated with another user pool` | `COGNITO_DOMAIN_PREFIX` bị trùng toàn cầu trong `ap-southeast-1` hoặc chứa từ cấm (`aws`, `amazon`, `cognito`). | Đổi `COGNITO_DOMAIN_PREFIX` sang chuỗi khác duy nhất (chữ thường, số, dấu `-`). |
| `redirect_mismatch` khi đăng nhập từ Next.js | Next.js cấu hình `trailingSlash: true` nên redirect URI là `/auth/callback/` (có dấu `/` cuối), nhưng cấu hình thiếu `/` hoặc thiếu domain mới. | Đảm bảo `NEXT_PUBLIC_COGNITO_REDIRECT_URI` có dấu `/` cuối và chạy lại `APP_DOMAIN=<domain> ./infra/cognito/setup-cognito.sh`. |
| `Missing SSM parameters` khi chạy trên Git Bash (Windows) | MSYS2 tự đổi `/exammaker/...` thành `C:/Program Files/Git/exammaker/...`. | Hai script đã tự đặt `MSYS_NO_PATHCONV=1`. Với lệnh `aws ssm` gõ tay trên Git Bash, thêm `MSYS_NO_PATHCONV=1` trước lệnh. |
| `Permission denied` khi chạy `./infra/cognito/setup-cognito.sh` | Mất bit thực thi sau khi tải thủ công hoặc upload lên CloudShell. | Chạy `chmod +x infra/cognito/setup-cognito.sh infra/cognito/verify-cognito.sh` (và `sed -i 's/\r$//' ...` nếu copy từ Windows). |
| `Existing App Client ... has a ClientSecret` | App Client trùng tên đã được tạo tay có kèm secret (Cognito không cho phép gỡ secret khi update). | Xoá App Client cũ trên Console rồi chạy lại script, hoặc chọn `APP_CLIENT_NAME` mới. |
| Không nhận được email mật khẩu tạm / quên mật khẩu | Vượt giới hạn **50 email/ngày** của `COGNITO_DEFAULT` hoặc rơi vào thư mục Spam. | Kiểm tra hộp thư Spam; nếu thực sự vượt 50 email/ngày, kiểm tra hạn mức gửi email trong Cognito hoặc cân nhắc nối Amazon SES (ngoài phạm vi MVP). |

---

## 8. Lưu ý chi phí

- Cognito `ESSENTIALS` miễn phí **10.000 MAU/tháng** (Always Free). Với quy mô dự án dưới 100 tài khoản giáo viên, chi phí là **$0.00/tháng**.
- `COGNITO_DEFAULT` email có giới hạn gửi **50 email/ngày** (đủ cho quy mô demo vài chục giáo viên; không cần dựng Amazon SES trong MVP).
- SSM Parameter Store loại `Standard` (`String`) miễn phí **$0.00/tháng**.
