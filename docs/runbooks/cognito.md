# Runbook — Triển khai Amazon Cognito User Pool (Task O9)

Tài liệu hướng dẫn dựng, kiểm tra và bàn giao **Amazon Cognito User Pool** cho dự án **AI Exam Maker** theo đúng kiến trúc đã chốt tại `docs/context.md` (mục 3.3, 5.2, 5.4, 9.2, 14).

> **Quy tắc bảo mật:** Tuyệt đối **không** commit `UserPoolId`, `ClientId`, AWS Account ID, ARN thật hay thông tin user thật vào repository, và **không** gửi ID thật qua nhóm chat công khai. Mọi thông số được bàn giao cho DEV-A (`A4`) và DEV-B (`B2`) thông qua **AWS Systems Manager Parameter Store**.

---

## 1. Thông số kiến trúc chuẩn

| Thành phần | Giá trị chuẩn | Tham chiếu |
|---|---|---|
| Region | `ap-southeast-1` (Singapore) | `docs/context.md` §14 |
| Gói Cognito (Tier) | `ESSENTIALS` (hỗ trợ Managed Login v2, **Always Free ≤ 10.000 MAU**) | `docs/context.md` §6 |
| Tự đăng ký (Self sign-up) | **Tắt** (`AllowAdminCreateUserOnly = true`) | `docs/context.md` §3.3 |
| Định danh đăng nhập | `email` (không phân biệt hoa/thường), tự xác minh email | `docs/context.md` §3.3 |
| 3 Nhóm người dùng (Groups) | `admin` (precedence 1), `head` (2), `teacher` (3) — chữ thường | `docs/context.md` §3, `openapi.yaml` |
| App Client | Public SPA (**không tạo Client Secret**), chỉ bật OAuth flow `code` (**PKCE**) | `docs/context.md` §5.4, §9.2 |
| OAuth Scopes | `openid`, `email`, `profile` | `docs/context.md` §9.2 |
| Token TTL | Access token: **60 phút** · ID token: **60 phút** · Refresh token: **7 ngày** | `docs/context.md` §3.3 |
| Callback URL (`trailingSlash: true`) | Local: `http://localhost:3000/auth/callback/` · Prod: `https://app.<domain>/auth/callback/` | `frontend/next.config.ts` |
| Logout URL | Local: `http://localhost:3000/` · Prod: `https://app.<domain>/` | `docs/context.md` §9.2 |
| Quên mật khẩu & đổi mật khẩu lần đầu | Managed Login v2 + khôi phục qua `verified_email` (`COGNITO_DEFAULT`) | `docs/context.md` §3.3 |
| Bảo mật bổ sung | `PreventUserExistenceErrors = ENABLED`, `EnableTokenRevocation = true` | `docs/context.md` §10 |

---

## 2. Cách 1: Dựng tự động bằng AWS CLI script (Khuyên dùng)

Script `infra/cognito/setup-cognito.sh` có tính **idempotent** (chạy lại nhiều lần không tạo trùng tài nguyên; dùng lại được khi cần bổ sung `APP_DOMAIN` sau task `O4`). Chạy tốt trên **AWS CloudShell** (trong AWS Console, chọn region `ap-southeast-1`), Linux, macOS, hoặc Git Bash trên Windows.

### Bước 1: Dựng cho môi trường `dev` (mở khóa ngay cho DEV-A và DEV-B)

Chọn một tiền tố domain Cognito duy nhất toàn vùng `ap-southeast-1` (chữ thường, số, dấu `-`, không chứa chữ `aws`/`amazon`/`cognito`):

```bash
COGNITO_DOMAIN_PREFIX="exammaker-dev-<ten-duy-nhat>" \
  ./infra/cognito/setup-cognito.sh
```

### Bước 2: Cập nhật thêm domain thật (sau khi OPS-1 hoàn tất task `O4`)

Chạy lại đúng script với biến `APP_DOMAIN` (giữ nguyên `ENV_NAME` muốn cập nhật):

```bash
# Môi trường dev (giữ cả localhost và app.<domain>)
APP_DOMAIN="<domain-cua-nhom>" \
  ./infra/cognito/setup-cognito.sh

# Môi trường prod (chỉ cho phép https://app.<domain>/auth/callback/, bỏ localhost)
ENV_NAME=prod INCLUDE_LOCALHOST=false APP_DOMAIN="<domain-cua-nhom>" \
  COGNITO_DOMAIN_PREFIX="exammaker-prod-<ten-duy-nhat>" \
  ./infra/cognito/setup-cognito.sh
```

---

## 3. Cách 2: Dựng thủ công trên AWS Console

Nếu dựng trực tiếp trên AWS Management Console tại region **`ap-southeast-1`**:

1. **Tạo User Pool** (*Amazon Cognito → User pools → Create user pool*):
   - Application type: **Single-page application (SPA)**, tên client `exammaker-dev-spa`.
   - Sign-in identifiers: chọn **Email**.
   - Self-registration: **Bỏ chọn / Tắt** (*Enable self-registration = OFF*).
   - Required attributes: `email`; thêm mutable attribute `name`.
   - Return URL (Callback URL): `http://localhost:3000/auth/callback/`.
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
   - **Login pages**: Callback URL `http://localhost:3000/auth/callback/` (và `https://app.<domain>/auth/callback/` khi có domain); Sign-out URL `http://localhost:3000/`; Identity provider `Cognito user pool`; OAuth grant types **chỉ chọn `Authorization code grant`**; OpenID Connect scopes: `openid`, `email`, `profile`.
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
aws ssm get-parameters-by-path \
  --region ap-southeast-1 \
  --path "/exammaker/dev/Cognito" \
  --query "Parameters[].{Name:Name,Value:Value}" \
  --output table
```

### Ánh xạ sang cấu hình ứng dụng

- **Backend (.NET 10 — `A3`, `A4`):**
  - Chạy trên EC2 (tự nạp từ SSM khi đặt `ParameterStore__Path=/exammaker/<env>`): đọc qua section `Cognito` (`Cognito:Region`, `Cognito:UserPoolId`, `Cognito:ClientId`, `Cognito:Authority`).
  - Chạy local không cần SSM: đặt biến môi trường `Cognito__Region`, `Cognito__UserPoolId`, `Cognito__ClientId`, `Cognito__Authority` (không commit vào repo).
  - Quyền IAM tối thiểu cho EC2 Instance Profile (tác vụ `O3`): `cognito-idp:AdminCreateUser`, `cognito-idp:AdminAddUserToGroup`, `cognito-idp:AdminRemoveUserFromGroup`, `cognito-idp:AdminListGroupsForUser`, `cognito-idp:AdminDisableUser`, `cognito-idp:AdminEnableUser`, `cognito-idp:AdminUserGlobalSignOut`, `cognito-idp:AdminUpdateUserAttributes`, `cognito-idp:ListUsers` — giới hạn `Resource` chỉ tới ARN của User Pool này.
- **Frontend (Next.js static export — `B2`):**
  - Biến môi trường build-time trong `.env.local` (chỉ nằm ở local hoặc GitHub Actions Variables khi deploy):
    - `NEXT_PUBLIC_COGNITO_DOMAIN` = giá trị của `/exammaker/<env>/Cognito/HostedUiDomain`
    - `NEXT_PUBLIC_COGNITO_CLIENT_ID` = giá trị của `/exammaker/<env>/Cognito/ClientId`
    - `NEXT_PUBLIC_COGNITO_REDIRECT_URI` = `http://localhost:3000/auth/callback/` (hoặc `https://app.<domain>/auth/callback/`)
    - `NEXT_PUBLIC_COGNITO_LOGOUT_URI` = `http://localhost:3000/` (hoặc `https://app.<domain>/`)

---

## 5. Quy trình kiểm thử (Verification)

### 5.1 Kiểm tra tự động bằng script

```bash
./infra/cognito/verify-cognito.sh
```

Script xác minh toàn bộ 9 điều kiện: đủ tham số SSM, `AllowAdminCreateUserOnly=true`, `verified_email` recovery, đủ 3 group (`admin`, `head`, `teacher`), không có `ClientSecret`, chỉ bật OAuth flow `code`, Access token TTL 60 phút, `PreventUserExistenceErrors=ENABLED`, `EnableTokenRevocation=true`, callback URL có `/auth/callback/`, và OIDC discovery endpoint trả HTTP 200.

### 5.2 Kiểm tra thủ công luồng mật khẩu tạm & quên mật khẩu

1. Tạo một tài khoản kiểm thử bằng quyền admin (không dùng email cá nhân thật vào repo):
   ```bash
   USER_POOL_ID="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Cognito/UserPoolId --query Parameter.Value --output text)"
   aws cognito-idp admin-create-user \
     --region ap-southeast-1 \
     --user-pool-id "$USER_POOL_ID" \
     --username "<test-email>" \
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

## 6. Lưu ý chi phí

- Cognito `ESSENTIALS` miễn phí **10.000 MAU/tháng** (Always Free). Với quy mô dự án dưới 100 tài khoản giáo viên, chi phí là **$0.00/tháng**.
- `COGNITO_DEFAULT` email có giới hạn gửi **50 email/ngày** (đủ cho quy mô demo vài chục giáo viên; không cần dựng Amazon SES trong MVP).
- SSM Parameter Store loại `Standard` (`String`) miễn phí **$0.00/tháng**.
