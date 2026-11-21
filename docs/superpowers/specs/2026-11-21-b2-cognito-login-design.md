# B2 — Đăng nhập Cognito (Authorization Code + PKCE) — Thiết kế

> Ngày: 21/11/2026 · Người phụ trách: DEV-B · Issue: B2 · Phụ thuộc: B1 (#12), O9 (#21), O4 (#19) — đã đóng.
> Ngữ cảnh: `docs/context.md` mục 3.3, 5.4, 9.2, 10; `docs/a3-auth.md` (hợp đồng 401 phía API).

## 1. Mục đích

Giáo viên/tổ trưởng/admin đăng nhập qua Managed Login của Cognito; frontend (Next.js static export) giữ phiên, gắn **access token** vào mọi lệnh gọi `/api/v1`, tự làm mới token, xử lý hết hạn/bị khóa, đăng xuất sạch. Đây là nền cho mọi màn hình sau (B5–B10, A10) và luồng demo chính (mục 2.1).

## 2. Mục tiêu / Không làm / Phản mục tiêu

**Mục tiêu**
- Đăng nhập PKCE, callback `/auth/callback/`, quay lại đúng trang đang xem.
- Làm mới token tự động; refresh trang giữa chừng không mất phiên (trong cùng tab).
- 401 → thử refresh **một lần**, gọi lại **một lần**; vẫn 401 → xóa phiên, báo liên hệ quản trị viên (a3-auth.md dòng 85).
- Đăng xuất: thu hồi refresh token + xóa phiên Cognito.
- Auth guard (chỉ UX) + ẩn/hiện menu theo `GET /me`.
- CSP chặt cho bản build tĩnh.

**Không làm trong B2**
- Màn hình quản lý user (A4/A10), đổi mật khẩu/quên mật khẩu tự dựng (dùng Managed Login của Cognito).
- Đồng bộ phiên giữa nhiều tab; SSO/IdP ngoài.
- Gắn header CSP/HSTS ở CloudFront (thuộc O5 — B2 chỉ cung cấp cấu hình đề xuất).

**Phản mục tiêu (dù chạy được vẫn coi là hỏng)**
- Token/refresh token xuất hiện trong log, URL, `localStorage`, hoặc repo.
- Vòng lặp redirect/refresh vô hạn khi user bị khóa hoặc chưa có trong DB.
- Frontend tự quyết quyền thay API (guard chỉ ẩn/hiện, không là hàng rào bảo mật).
- Open redirect qua tham số "quay lại trang".
- CSP có `'unsafe-inline'`/`'unsafe-eval'` cho script trong bản build.

## 3. Ràng buộc

- Static export: không middleware/SSR/route động; route bằng query string; `trailingSlash: true`.
- App Client công khai (không secret), scopes `openid email profile`, access token 60 phút, refresh 7 ngày, `EnableTokenRevocation=true`, callback `…/auth/callback/`, logout URL `…/` (xem `infra/cognito/setup-cognito.sh`).
- Không commit ID thật: domain Cognito và ClientId vào bản build qua biến môi trường lúc build (CI/O7 lấy từ Parameter Store; local dùng `.env.local` không commit, repo chỉ có `.env.example`).
- Chuỗi giao diện ở `src/messages/vi.ts`; code/comment tiếng Anh.
- Dùng seam có sẵn của B1: `setAccessTokenProvider` trong `src/lib/api/auth-token.ts`, middleware trong `src/lib/api/client.ts`.

## 4. Các hướng đã cân nhắc

| Hướng | Ưu | Nhược | Kết luận |
|---|---|---|---|
| **1. Tự viết (~200–300 dòng, Web Crypto + fetch tới endpoint Cognito)** | Không thêm dependency; khớp chính xác cách lưu token đã chọn, seam B1 và quy tắc "refresh một lần" | Tự test state, single-flight refresh | **Đã chọn** |
| 2. `oidc-client-ts` | PKCE/state/silent renew có sẵn | Lưu cả gói token một chỗ → phải viết store riêng; Cognito không khai báo đúng chuẩn end-session/revocation → vẫn phải cấu hình tay | Loại |
| 3. AWS Amplify Auth | Tích hợp AWS sẵn | Nặng, mặc định `localStorage`, nhiều phần thừa với static export | Loại |

**Lưu token (đã chọn — phương án A):** access token **chỉ trong bộ nhớ**; refresh token trong **`sessionStorage`** (sống theo tab). Lý do: refresh trang không mất phiên, đóng tab là hết, phạm vi rủi ro XSS nhỏ hơn `localStorage`; bù bằng CSP chặt. Đã loại: chỉ bộ nhớ (mỗi lần refresh trang phải quay vòng Cognito) và `localStorage` (refresh token 7 ngày dùng được ở mọi tab, rủi ro cao nhất).

## 5. Thiết kế

### 5.1 Cấu hình (build-time, công khai)
- `NEXT_PUBLIC_COGNITO_DOMAIN` (vd `https://<prefix>.auth.<region>.amazoncognito.com`), `NEXT_PUBLIC_COGNITO_CLIENT_ID`.
- `redirect_uri` và `logout_uri` tính từ `window.location.origin` (`/auth/callback/`, `/`) — tự khớp localhost/`app.<domain>`.
- Thiếu cấu hình → trang báo "chưa cấu hình đăng nhập" thay vì crash; build vẫn chạy (CI không có giá trị thật).

### 5.2 Module (đề xuất, trong `src/lib/auth/`)
- `pkce.ts` — tạo `code_verifier` (32 byte ngẫu nhiên, base64url), `code_challenge` S256, `state`.
- `cognito.ts` — dựng URL authorize/logout; gọi `/oauth2/token` (`authorization_code`, `refresh_token`) và `/oauth2/revoke` (form-urlencoded, `client_id`, không secret).
- `session.ts` — giữ access token + thời điểm hết hạn trong bộ nhớ; refresh token trong `sessionStorage`; **single-flight refresh** (nhiều request cùng lúc chỉ gọi refresh một lần); làm mới chủ động khi còn ≤ 5 phút; đăng ký `setAccessTokenProvider`.
- `return-to.ts` — chỉ chấp nhận đường dẫn tương đối cùng origin bắt đầu bằng `/` và không bắt đầu bằng `//`; ngược lại về `/`.
- `AuthProvider` (React context) — trạng thái `loading | signedOut | signedIn | denied | misconfigured`, `me` từ `GET /me`, `login()`, `logout()`.

### 5.3 Luồng
**Đăng nhập:** `login(returnTo)` → lưu `{state, codeVerifier, returnTo}` vào `sessionStorage` (khóa tạm, xóa sau callback) → chuyển tới `/oauth2/authorize?response_type=code&client_id&redirect_uri&scope=openid email profile&state&code_challenge&code_challenge_method=S256`.

**Callback (`/auth/callback/`):** đọc `code`, `state`, `error` từ query → `state` phải khớp và chưa dùng (sai/thiếu → báo lỗi, không đổi code) → đổi code lấy token → lưu theo 5.2 → xóa dữ liệu tạm và dọn query khỏi URL (`history.replaceState`) → gọi `/me` → điều hướng `returnTo`. `error=…` từ Cognito → hiển thị lỗi tiếng Việt + nút thử lại.

**Khởi động trang / refresh trang:** có refresh token trong `sessionStorage` → gọi refresh để lấy access token mới → `/me`. Không có → `signedOut`.

**Gọi API:** middleware gắn `Authorization: Bearer`. Gặp 401: nếu request chưa retry → refresh (single-flight) → gửi lại **một lần** bằng bản sao request đã lưu trước khi gửi. Vẫn 401 hoặc refresh thất bại (`invalid_grant`) → xóa phiên → trạng thái:
- refresh thất bại (phiên hết hạn/bị thu hồi) → `signedOut`, guard đưa về đăng nhập với `returnTo` là trang hiện tại;
- refresh thành công nhưng API vẫn 401 (user chưa có trong DB / bị khóa) → `denied`: trang "Tài khoản chưa được cấp quyền hoặc đã bị khóa, liên hệ quản trị viên" + nút đăng xuất; **không** tự đăng nhập lại.

403 không đụng phiên — để màn hình xử lý (`Forbidden`, `SelfReview`).

**Đăng xuất:** gọi `/oauth2/revoke` với refresh token (lỗi mạng không chặn đăng xuất) → xóa bộ nhớ + `sessionStorage` + cache TanStack Query → chuyển tới `${domain}/logout?client_id&logout_uri=<origin>/`.

**ID token:** không lưu, không dùng (API chỉ nhận access token; thông tin hiển thị lấy từ `/me`). Vì không dùng ID token nên không cần `nonce`; chống tráo code dựa vào PKCE + `state`.

### 5.4 Auth guard (UX)
- `AuthGate` bọc layout. Công khai: `/auth/callback/`. Mọi trang khác cần đăng nhập: `signedOut` → tự gọi `login(trang hiện tại)`; `loading` → "Đang tải…"; `denied`/`misconfigured` → trang thông báo tương ứng.
- Header hiển thị họ tên, vai trò, tổ (từ `/me`) và nút **Đăng xuất**; helper `hasRole(...)` cho màn hình sau ẩn/hiện menu. Ghi rõ trong code: chỉ là UX, quyền thật ở API.

### 5.5 CSP
- **Trong B2:** thẻ `<meta http-equiv="Content-Security-Policy">` chèn **đầu `<head>`** mỗi file HTML trong `out/` bằng script hậu build (`scripts/inject-csp.mjs`, chạy sau `next build`), tính `sha256` cho từng script inline của trang đó.
  - `default-src 'self'; script-src 'self' 'sha256-…'; style-src 'self' 'unsafe-inline'` (KaTeX dùng thuộc tính `style`); `img-src 'self' data:; font-src 'self'; connect-src 'self' <COGNITO_DOMAIN> [<API_BASE_URL> nếu khác origin]; object-src 'none'; base-uri 'self'; form-action 'self' <COGNITO_DOMAIN>`.
  - `next dev` không áp CSP (cần eval/HMR).
- **Đề xuất cho O5** (ghi trong tài liệu B2): Response Headers Policy CloudFront cho behavior `/*` thêm `frame-ancestors 'none'` (meta không hỗ trợ), HSTS, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`.

### 5.6 Kiểm thử
- Thêm **Vitest + jsdom** và script `npm test` (CI đã chạy `npm test --if-present`).
- Unit: PKCE (độ dài, S256 đúng vector RFC 7636), `state` sai/thiếu/dùng lại, `return-to` chặn open redirect, single-flight refresh, 401 → retry đúng một lần, `denied` vs `signedOut`, logout gọi revoke + dựng URL đúng, không có token trong `localStorage`; `inject-csp` tính hash đúng và chèn đầu `<head>`.
- Smoke test thủ công với Cognito thật (ghi kết quả vào tài liệu, không lưu token/ID): đăng nhập, refresh trang giữa chừng, ép hết hạn access token, user bị khóa → 401 → `denied`, đăng xuất rồi bấm Back, callback sai `state`, kiểm console không có vi phạm CSP.

### 5.7 Tài liệu
- `docs/context.md` mục 9.2: thêm "Chi tiết triển khai B2" (nơi lưu token, quy tắc 401, CSP meta + đề xuất header O5) và dòng lịch sử thay đổi.
- `docs/b2-auth-frontend.md`: cấu hình env, luồng, bảng hàm chính, smoke test.
- `frontend/.env.example`; tick B2 trong `docs/tasks.md` khi PR merge + CI xanh.

## 6. Quyết định đã chốt với người phụ trách
- Lưu token phương án A (bộ nhớ + `sessionStorage`) — 21/11/2026.
- Tự viết luồng PKCE, không thêm thư viện OIDC — 21/11/2026.

## 7. Quyết định do người viết thiết kế đưa ra (có thể sửa)
- CSP bằng meta + hash hậu build; header CloudFront giao O5.
- Không dùng ID token/nonce; thông tin user lấy từ `/me`.
- Làm mới chủ động khi còn ≤ 5 phút; xử lý nếu Cognito trả refresh token mới (rotation) thì thay thế.
- Mọi trang cần đăng nhập trừ callback; tự chuyển sang Cognito khi chưa đăng nhập.
- Mỗi tab một phiên; tab mới quay vòng Cognito (thường không phải gõ lại mật khẩu nhờ cookie Managed Login).
- Hết phiên giữa lúc đang nhập form sẽ mất dữ liệu chưa lưu — chấp nhận trong MVP.
- Vitest + jsdom làm khung test frontend.

## 8. Để người triển khai tự quyết
Tên file/thành phần cụ thể, cách tổ chức context/hook, giao diện chi tiết trang lỗi, cách parse HTML trong `inject-csp.mjs`.
