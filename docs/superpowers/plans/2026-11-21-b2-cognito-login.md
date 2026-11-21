# B2 Cognito Login (Authorization Code + PKCE) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đăng nhập Cognito bằng Authorization Code + PKCE cho frontend static export: callback, làm mới token, xử lý 401/hết hạn/bị khóa, đăng xuất, auth guard (UX) và CSP chặt.

**Architecture:** Thư viện tự viết trong `frontend/src/lib/auth/` (PKCE, endpoint Cognito, luồng login, session). Access token chỉ trong bộ nhớ, refresh token trong `sessionStorage`. Middleware openapi-fetch gắn token và thử refresh + gọi lại đúng một lần khi 401. React `AuthProvider` + `AuthGate` lo UX. CSP là thẻ meta chèn sau build với hash script inline.

**Tech Stack:** Next.js 16 static export, React 19, TypeScript, openapi-fetch, TanStack Query, Web Crypto, Vitest + jsdom + Testing Library.

**Spec:** `docs/superpowers/specs/2026-11-21-b2-cognito-login-design.md`

## Global Constraints

- **KHÔNG `git commit` / `git push`.** Người phụ trách tự commit. Cuối mỗi task chỉ có bước "Checkpoint" (chạy test + báo kết quả).
- Làm trên nhánh `feat/b2-cognito-login` tách từ `dev`.
- Static export: không middleware/SSR/route động; route có `trailingSlash` (`/auth/callback/`).
- Access token **chỉ trong bộ nhớ**; refresh token **chỉ trong `sessionStorage`** (key `exammaker.auth.refresh`); không bao giờ `localStorage`, URL, log. ID token không lưu, không dùng; không dùng `nonce`.
- Scope `openid email profile`; `redirect_uri = ${location.origin}/auth/callback/`; `logout_uri = ${location.origin}/`.
- Cấu hình build-time: `NEXT_PUBLIC_COGNITO_DOMAIN`, `NEXT_PUBLIC_COGNITO_CLIENT_ID`. Không commit giá trị thật; chỉ `frontend/.env.example`.
- 401: refresh **một lần**, gọi lại **một lần**; refresh `invalid_grant` → `signedOut`; refresh OK nhưng vẫn 401 → `denied` (không tự đăng nhập lại). 403 không đụng phiên.
- Làm mới khi access token còn ≤ 5 phút (`REFRESH_MARGIN_MS = 300_000`).
- Chuỗi giao diện chỉ ở `src/messages/vi.ts` (tiếng Việt); code/comment tiếng Anh.
- CSP build: không `'unsafe-inline'`/`'unsafe-eval'` trong `script-src`.

## Review Focus

1. React StrictMode chạy effect callback hai lần → lần hai gặp `state_mismatch` vì dữ liệu tạm đã xóa. Kỳ vọng: chỉ đổi code một lần, không hiện lỗi. Test ở Task 5.
2. Đăng xuất: `clearSession` làm `AuthGate` tự chuyển sang trang đăng nhập trước khi kịp tới `/logout` của Cognito. Kỳ vọng: điều hướng duy nhất là URL logout. Test ở Task 5.
3. Nhiều query cùng nhận 401 → chỉ một lần gọi refresh, mỗi request gọi lại đúng một lần. Test ở Task 4.
4. Mất mạng khi refresh → không xóa phiên, không chuyển `denied`. Test ở Task 3 và 4.
5. Mở `/auth/callback/` không có dữ liệu tạm (bookmark/tab mới) hoặc có `error=access_denied` → báo lỗi tiếng Việt + nút thử lại, không vòng lặp. Test ở Task 2 và 5.

---

### Task 1: Khung test + PKCE + chống open redirect

**Files:**
- Modify: `frontend/package.json` (script `"test": "vitest run"`, devDeps)
- Create: `frontend/vitest.config.mts`, `frontend/src/lib/auth/pkce.ts`, `frontend/src/lib/auth/return-to.ts`
- Test: `frontend/src/lib/auth/pkce.test.ts`, `frontend/src/lib/auth/return-to.test.ts`

**Interfaces:**
- Produces:
  - `base64UrlEncode(bytes: Uint8Array): string`
  - `randomUrlSafe(byteLength: number): string`
  - `createCodeChallenge(verifier: string): Promise<string>` (S256)
  - `createPkcePair(): Promise<{ verifier: string; challenge: string }>` (verifier từ 32 byte)
  - `createState(): string` (từ 16 byte)
  - `sanitizeReturnTo(value: string | null | undefined): string`

- [ ] **Step 0: Tạo nhánh** — `git switch dev && git pull && git switch -c feat/b2-cognito-login`
- [ ] **Step 1: Cài khung test** — `cd frontend && npm install -D vitest jsdom @testing-library/react @testing-library/dom`; `vitest.config.mts` dùng `environment: "jsdom"`, alias `@` → `./src`; thêm script `"test": "vitest run"`.
- [ ] **Step 2: Viết test thất bại**

```ts
// pkce.test.ts
it("matches RFC 7636 appendix B vector", async () => {
  expect(await createCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"))
    .toBe("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
});
it("creates a 43-char url-safe verifier and distinct states", async () => {
  const { verifier } = await createPkcePair();
  expect(verifier).toMatch(/^[A-Za-z0-9_-]{43}$/);
  expect(createState()).not.toBe(createState());
});

// return-to.test.ts
it.each([
  ["/exams/view/?id=1#q", "/exams/view/?id=1#q"],
  [null, "/"], ["", "/"], ["exams", "/"],
  ["//evil.example/x", "/"], ["/\\evil.example", "/"],
  ["https://evil.example/", "/"], ["javascript:alert(1)", "/"],
  ["/auth/callback/?code=x", "/"],
])("sanitizeReturnTo(%s) -> %s", (input, expected) => {
  expect(sanitizeReturnTo(input)).toBe(expected);
});
```

- [ ] **Step 3: Chạy** `npm test` → FAIL (module chưa có).
- [ ] **Step 4: Cài đặt** `pkce.ts` bằng `crypto.getRandomValues` + `crypto.subtle.digest("SHA-256")`; `return-to.ts`: phải bắt đầu `/`, không `//` hay `/\`, `new URL(value, location.origin).origin === location.origin`, trả `pathname+search+hash`; đường dẫn bắt đầu `/auth/callback` → `/`.
- [ ] **Step 5: Chạy** `npm test` → PASS.
- [ ] **Checkpoint:** `npm run lint && npm run typecheck && npm test` xanh; báo người phụ trách (không commit).

### Task 2: Cấu hình + endpoint Cognito + luồng đăng nhập/callback

**Files:**
- Create: `frontend/src/lib/auth/config.ts`, `frontend/src/lib/auth/cognito.ts`, `frontend/src/lib/auth/login.ts`, `frontend/.env.example`
- Test: `frontend/src/lib/auth/cognito.test.ts`, `frontend/src/lib/auth/login.test.ts`

**Interfaces:**
- Consumes: Task 1 (`createPkcePair`, `createState`, `sanitizeReturnTo`); `setTokens` từ Task 3 (login.ts import; trong test thì mock `./session`).
- Produces:
  - `interface AuthConfig { domain: string; clientId: string }`; `getAuthConfig(): AuthConfig | null` (thiếu biến hoặc domain không phải `https://` → `null`; bỏ `/` cuối)
  - `redirectUri(): string`, `logoutUri(): string`
  - `type TokenSet = { accessToken: string; expiresIn: number; refreshToken?: string }`
  - `class TokenEndpointError extends Error { readonly code: string; readonly status: number }`
  - `buildAuthorizeUrl(cfg: AuthConfig, p: { state: string; codeChallenge: string }): string`
  - `buildLogoutUrl(cfg: AuthConfig): string`
  - `exchangeCode(cfg: AuthConfig, p: { code: string; codeVerifier: string }): Promise<TokenSet>`
  - `refreshTokens(cfg: AuthConfig, refreshToken: string): Promise<TokenSet>`
  - `revokeToken(cfg: AuthConfig, refreshToken: string): Promise<void>`
  - `startLogin(returnTo: string): Promise<void>` (lưu `{ state, verifier, returnTo }` vào `sessionStorage["exammaker.auth.pending"]`, rồi `location.assign`)
  - `class LoginCallbackError extends Error { readonly code: "missing_pending" | "state_mismatch" | "missing_code" | "cognito_error" | "token_exchange_failed" }`
  - `completeLogin(search: URLSearchParams): Promise<{ returnTo: string }>` (luôn xóa pending; gọi `setTokens`)

- [ ] **Step 1: Viết test thất bại** (mock `fetch` bằng `vi.stubGlobal`)
  - `buildAuthorizeUrl` có đủ `response_type=code`, `client_id`, `redirect_uri=http://localhost:3000/auth/callback/`, `scope=openid email profile`, `state`, `code_challenge`, `code_challenge_method=S256`; đường dẫn `/oauth2/authorize`.
  - `buildLogoutUrl` = `${domain}/logout?client_id=…&logout_uri=http%3A%2F%2Flocalhost%3A3000%2F`.
  - `exchangeCode` POST `/oauth2/token`, `Content-Type: application/x-www-form-urlencoded`, body có `grant_type=authorization_code`, `code_verifier`, không có `client_secret`; map `{access_token, expires_in, refresh_token}` → `TokenSet`; bỏ qua `id_token`.
  - Phản hồi 400 `{"error":"invalid_grant"}` → `TokenEndpointError` với `code === "invalid_grant"`, `status === 400`; lỗi mạng (`fetch` reject) ném lại nguyên lỗi (không bọc thành `TokenEndpointError`).
  - `refreshTokens` gửi `grant_type=refresh_token`; `refreshToken` undefined khi Cognito không trả về.
  - `revokeToken` POST `/oauth2/revoke` với `token` + `client_id`.
  - `getAuthConfig()` trả `null` khi thiếu biến hoặc domain `http://…`.
  - `completeLogin`: state sai → `state_mismatch`; không có pending → `missing_pending`; `?error=access_denied` → `cognito_error`; không có `code` → `missing_code`; sau mọi kết quả `sessionStorage` không còn key pending; thành công → gọi `setTokens` một lần, trả `returnTo` đã sanitize.
- [ ] **Step 2: Chạy** `npm test` → FAIL.
- [ ] **Step 3: Cài đặt** các hàm trên; `.env.example` có hai biến với giá trị giả `https://example-prefix.auth.ap-southeast-1.amazoncognito.com` và `example-client-id`.
- [ ] **Step 4: Chạy** `npm test` → PASS.
- [ ] **Checkpoint:** lint + typecheck + test xanh; không commit.

### Task 3: Session (token trong bộ nhớ, refresh single-flight)

**Files:**
- Create: `frontend/src/lib/auth/session.ts`
- Test: `frontend/src/lib/auth/session.test.ts`

**Interfaces:**
- Consumes: Task 2 (`getAuthConfig`, `refreshTokens`, `TokenSet`, `TokenEndpointError`); `setAccessTokenProvider` (`src/lib/api/auth-token.ts`).
- Produces:
  - `type SessionStatus = "unknown" | "signedIn" | "signedOut" | "denied"`
  - `setTokens(t: TokenSet): void` → `signedIn`; lưu refresh token mới nếu có (rotation)
  - `getValidAccessToken(): Promise<string | null>` (còn ≤ `REFRESH_MARGIN_MS` → `forceRefresh`)
  - `forceRefresh(): Promise<string | null>` — single-flight; không có refresh token → `null`; `TokenEndpointError` → `clearSession("signedOut")`, trả `null`; lỗi mạng → **ném lại, giữ phiên**
  - `restoreSession(): Promise<SessionStatus>` (có refresh token → `forceRefresh`)
  - `clearSession(next: "signedOut" | "denied"): void` (xóa bộ nhớ + key refresh)
  - `getRefreshToken(): string | null`, `getStatus(): SessionStatus`
  - `subscribeSession(listener: (s: SessionStatus) => void): () => void`
  - `installAccessTokenProvider(): void`
  - `__resetSessionForTests(): void`

- [ ] **Step 1: Viết test thất bại** (`vi.mock("./cognito")`, `vi.useFakeTimers()`)
  - `setTokens` không ghi gì vào `localStorage`; `sessionStorage["exammaker.auth.refresh"]` = refresh token; access token không nằm trong storage nào.
  - Token còn 10 phút → `getValidAccessToken` không gọi `refreshTokens`; còn 4 phút → gọi một lần.
  - Ba lệnh `forceRefresh()` song song → `refreshTokens` được gọi đúng 1 lần, cả ba nhận cùng token.
  - `refreshTokens` ném `TokenEndpointError("invalid_grant")` → trả `null`, status `signedOut`, key refresh bị xóa.
  - `refreshTokens` ném `TypeError("Failed to fetch")` → `forceRefresh` reject, status giữ `signedIn`, key refresh còn (Review Focus 4).
  - Rotation: lần refresh trả `refreshToken: "r2"` → storage = `"r2"`.
  - `restoreSession()` không có key → `signedOut`, không gọi mạng.
  - `subscribeSession` nhận đúng chuỗi trạng thái; hàm hủy đăng ký hoạt động.
- [ ] **Step 2: Chạy** → FAIL.
- [ ] **Step 3: Cài đặt** `session.ts` (state module-level; promise refresh dùng chung tới khi settle).
- [ ] **Step 4: Chạy** → PASS.
- [ ] **Checkpoint:** lint + typecheck + test xanh; không commit.

### Task 4: Middleware API — gắn token, 401 → refresh + gọi lại một lần

**Files:**
- Create: `frontend/src/lib/api/auth-middleware.ts`
- Modify: `frontend/src/lib/api/client.ts` (thay `authMiddleware` hiện có bằng `createAuthMiddleware()`)
- Test: `frontend/src/lib/api/auth-middleware.test.ts`

**Interfaces:**
- Consumes: `getAccessToken` (`auth-token.ts`), `forceRefresh`, `clearSession` (Task 3).
- Produces: `createAuthMiddleware(fetchImpl?: typeof fetch): Middleware`

Cách làm: `onRequest({ request, id })` gắn Bearer và giữ `request.clone()` trong `Map` theo `id`; `onResponse` xóa entry; nếu 401 → `forceRefresh()`; `null` → trả 401 gốc (phiên đã `signedOut`); lỗi mạng → trả 401 gốc, không đụng phiên; có token → gọi lại bản clone bằng `fetchImpl` (không qua middleware nên không lặp) với header mới; kết quả vẫn 401 → `clearSession("denied")`. `onError` xóa entry.

- [ ] **Step 1: Viết test thất bại** (dùng `createClient` thật với `fetch` giả)
  - Request có header `Authorization: Bearer t1`.
  - 401 rồi 200: `forceRefresh` 1 lần, gọi lại với `Bearer t2` và body POST giống hệt lần đầu; client nhận 200.
  - 401 rồi 401: `clearSession("denied")` 1 lần; tổng số lần gọi `fetch` = 2 (không lặp).
  - `forceRefresh` trả `null` → không gọi lại, không `clearSession("denied")`.
  - `forceRefresh` reject (mạng) → không gọi lại, không `clearSession`.
  - 403 → không refresh, không đụng phiên.
  - Ba GET song song cùng nhận 401 → `forceRefresh` (single-flight thật từ Task 3) gọi `refreshTokens` 1 lần, mỗi request gọi lại đúng 1 lần (Review Focus 3).
- [ ] **Step 2: Chạy** → FAIL.
- [ ] **Step 3: Cài đặt** và nối vào `client.ts`.
- [ ] **Step 4: Chạy** → PASS.
- [ ] **Checkpoint:** lint + typecheck + test xanh; không commit.

### Task 5: AuthProvider, trang callback, AuthGate, menu người dùng + đăng xuất

**Files:**
- Create: `frontend/src/lib/auth/auth-context.tsx`, `frontend/src/app/auth/callback/page.tsx`, `frontend/src/components/auth-gate.tsx`, `frontend/src/components/user-menu.tsx`
- Modify: `frontend/src/app/providers.tsx` (bọc `AuthProvider` trong `QueryClientProvider`), `frontend/src/app/layout.tsx` (`UserMenu` trong header, `AuthGate` quanh `main`), `frontend/src/messages/vi.ts`
- Test: `frontend/src/lib/auth/auth-context.test.tsx`, `frontend/src/app/auth/callback/page.test.tsx`, `frontend/src/components/auth-gate.test.tsx`

**Interfaces:**
- Consumes: Tasks 2–4; `api.GET("/me")`; `components["schemas"]["Me" | "Role"]` từ `src/lib/api/schema`.
- Produces:
  - `type AuthStatus = "loading" | "signedOut" | "signedIn" | "denied" | "misconfigured" | "error" | "loggingOut"`
  - `useAuth(): { status: AuthStatus; me: Me | null; login(returnTo?: string): Promise<void>; logout(): Promise<void>; retry(): void; hasRole(...roles: Role[]): boolean }`
  - `isPublicPath(pathname: string): boolean` (chỉ `/auth/callback/`)

Hành vi: mount → `getAuthConfig()` null → `misconfigured`; `installAccessTokenProvider()`; `restoreSession()`; `signedIn` → `/me` (401 do middleware → `denied`; lỗi mạng → `error`). Theo dõi `subscribeSession`. `logout()`: đặt `loggingOut` **trước**, `revokeToken` (bỏ qua lỗi), `clearSession("signedOut")`, `queryClient.clear()`, `location.assign(buildLogoutUrl(cfg))`. `AuthGate`: trang công khai → render con; `signedOut` → `login(pathname+search)`; `loggingOut`/`loading` → `vi.common.loading`; `denied`/`misconfigured`/`error` → trang thông báo. Callback page: `Suspense` + `useSearchParams`, `useRef` chặn chạy lần hai, `completeLogin` → `router.replace(returnTo)`; lỗi → thông báo + nút "Thử lại" gọi `login("/")`.

Chuỗi `vi.auth` (thêm vào `vi.ts`):
```ts
auth: {
  redirecting: "Đang chuyển tới trang đăng nhập…",
  completing: "Đang hoàn tất đăng nhập…",
  callbackFailed: "Đăng nhập không thành công. Vui lòng thử lại.",
  retry: "Thử lại",
  deniedTitle: "Tài khoản chưa được cấp quyền hoặc đã bị khóa",
  deniedBody: "Vui lòng liên hệ quản trị viên của trường để được hỗ trợ.",
  misconfigured: "Hệ thống chưa được cấu hình đăng nhập. Vui lòng liên hệ quản trị viên.",
  networkError: "Không kết nối được máy chủ. Vui lòng thử lại.",
  logout: "Đăng xuất",
  roles: { teacher: "Giáo viên", head: "Tổ trưởng", admin: "Quản trị" },
},
```

- [ ] **Step 1: Viết test thất bại** (mock module auth + `api`)
  - Callback page render trong `<StrictMode>` với `?code=c&state=s` → `completeLogin` gọi đúng 1 lần, `router.replace("/exams/view/?id=1")`, không có chữ `callbackFailed` (Review Focus 1).
  - Callback `completeLogin` reject `LoginCallbackError("missing_pending")` → hiện `callbackFailed` + nút `retry`; bấm → `startLogin("/")` (Review Focus 5).
  - `logout()` → `location.assign` gọi đúng 1 lần với URL logout; `startLogin` **không** được gọi (Review Focus 2); `queryClient` rỗng; `revokeToken` reject vẫn đăng xuất.
  - `AuthGate`: `signedOut` trên `/exams/view/?id=1` → `startLogin("/exams/view/?id=1")`; trên `/auth/callback/` → không gọi; `denied` → hiện `deniedTitle` + nút `logout`, không gọi `startLogin`.
  - `/me` trả 200 role `head` → `hasRole("head")` true, `UserMenu` hiện họ tên + "Tổ trưởng" + tên tổ.
  - `getAuthConfig()` null → `misconfigured`, không gọi mạng.
- [ ] **Step 2: Chạy** → FAIL.
- [ ] **Step 3: Cài đặt** các file; comment trong `AuthGate`/`hasRole`: UX only, API enforces authorization.
- [ ] **Step 4: Chạy** `npm test` → PASS; `npm run build` thành công khi **không** có biến Cognito.
- [ ] **Checkpoint:** lint + typecheck + test + build xanh; không commit.

### Task 6: CSP meta chèn sau build

**Files:**
- Create: `frontend/scripts/inject-csp.mjs`
- Modify: `frontend/package.json` (`"build": "next build && node scripts/inject-csp.mjs"`)
- Test: `frontend/scripts/inject-csp.test.mjs` (thêm `scripts/**/*.test.mjs` vào `include` của vitest)

**Interfaces:**
- Produces (export từ `inject-csp.mjs`):
  - `hashInlineScripts(html: string): string[]` — `'sha256-<base64>'` cho mỗi `<script>` không có `src`, hash đúng byte UTF-8 nội dung
  - `buildCsp(o: { scriptHashes: string[]; cognitoDomain?: string; apiOrigin?: string }): string`
  - `injectCsp(html: string, csp: string): string` — chèn ngay sau `<meta charSet="utf-8"/>` nếu có, không thì ngay sau thẻ mở `<head>`; ném lỗi nếu không có `<head>` hoặc đã có meta CSP
  - `main(outDir = "out")` — duyệt mọi `.html`; `cognitoDomain` từ `NEXT_PUBLIC_COGNITO_DOMAIN`; `apiOrigin` = origin của `NEXT_PUBLIC_API_BASE_URL` chỉ khi là URL tuyệt đối

Chính sách (giữ nguyên thứ tự):
```
default-src 'self'; script-src 'self' <hashes>; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' [cognitoDomain] [apiOrigin]; object-src 'none'; base-uri 'self'; form-action 'self' [cognitoDomain]
```

- [ ] **Step 1: Viết test thất bại**
  - `hashInlineScripts('<script>alert(1)</script><script src="/a.js"></script>')` → `["'sha256-bhHHL3z2vDgxUt0W3dWQOrprscmda2Y5pLsLg4GF+pI='"]` (đã kiểm bằng `printf 'alert(1)' | openssl dgst -sha256 -binary | base64`).
  - `buildCsp` không chứa `unsafe-eval`; `script-src` không chứa `unsafe-inline`; có `object-src 'none'`; bỏ phần tử rỗng khi không có `cognitoDomain`.
  - `injectCsp` đặt meta trước `<script>` đầu tiên; ném lỗi khi gọi hai lần.
- [ ] **Step 2: Chạy** → FAIL.
- [ ] **Step 3: Cài đặt** (regex là đủ cho HTML do Next sinh ra).
- [ ] **Step 4: Chạy** `npm test` → PASS; `npm run build` rồi `grep -c "Content-Security-Policy" out/index.html out/auth/callback/index.html` → mỗi file `1`.
- [ ] **Step 5: Kiểm tra trình duyệt** — `npx serve out -l 3000` (hoặc tương đương), mở `/`, DevTools console **không** có lỗi "Refused to execute inline script".
- [ ] **Checkpoint:** lint + typecheck + test + build xanh; không commit.

### Task 7: Tài liệu + smoke test Cognito thật

**Files:**
- Modify: `docs/context.md` (mục 9.2: "Chi tiết triển khai B2"; "Lịch sử thay đổi" bản 0.8 ngày 21/11/2026)
- Create: `docs/b2-auth-frontend.md`

- [ ] **Step 1: `context.md` 9.2** — thêm vài gạch đầu dòng: nơi lưu token (phương án A), không dùng ID token/nonce, quy tắc 401 (`signedOut` vs `denied`), mỗi tab một phiên, CSP meta + header đề xuất cho O5 (`frame-ancestors 'none'`, HSTS, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`).
- [ ] **Step 2: `b2-auth-frontend.md`** (tiếng Việt, theo mẫu `docs/a3-auth.md`): biến môi trường + cách đặt `.env.local`, sơ đồ luồng, bảng hàm chính, cách CSP sinh ra, checklist smoke test.
- [ ] **Step 3: Smoke test với Cognito thật** (người phụ trách chạy, ghi kết quả vào `b2-auth-frontend.md`, không ghi token/ID): đăng nhập → về đúng trang; refresh trang giữa chừng còn phiên; xóa access token trong bộ nhớ (reload sau khi đợi/ép hết hạn) → tự refresh; khóa user trong DB → trang `denied`; đăng xuất → Back không vào lại được; callback sửa `state` → báo lỗi; console không có vi phạm CSP.
- [ ] **Checkpoint:** `npm run api:check && npm run lint && npm run typecheck && npm test && npm run build` xanh; báo người phụ trách tự commit và mở PR vào `dev`. Tick B2 trong `docs/tasks.md` chỉ sau khi PR merge + CI xanh.
