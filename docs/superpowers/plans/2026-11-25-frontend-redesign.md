# Làm mới giao diện frontend: kế hoạch triển khai (PR 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Mục tiêu:** Áp bộ nhận diện "tấm bảng xanh ngọc" lên toàn bộ 14 trang frontend: token, font, component dùng chung, menu dọc có nút đóng/mở, trang chủ có mục "Cần bạn xử lý", và câu chữ viết lại. Hành vi, quyền và API client giữ nguyên.

**Kiến trúc:** Token màu và font khai báo bằng `@theme` của Tailwind v4 trong `globals.css`, font tự host qua `next/font/google`. Component trình bày nằm trong `src/components/ui/`, khung ứng dụng (menu dọc, trạng thái đóng/mở, tấm bảng đầu trang) nằm trong `src/components/shell/`. Ánh xạ trạng thái sang màu nằm trong một module thuần `src/lib/ui/status.ts`. Từng trang thay class tự do bằng các component này, logic dữ liệu giữ nguyên.

**Tech stack:** Next.js 16 (App Router, static export), React 19, Tailwind CSS v4, TanStack Query 5, vitest + Testing Library (jsdom).

**Spec:** [`docs/superpowers/specs/2026-11-25-frontend-redesign-design.md`](../specs/2026-11-25-frontend-redesign-design.md). Mockup: [`docs/superpowers/specs/assets/2026-11-25-frontend-redesign-mockup.html`](../specs/assets/2026-11-25-frontend-redesign-mockup.html) (mở bằng trình duyệt, bảng màu "Xanh ngọc").

**Phụ thuộc:** PR 1 ([`2026-11-25-exam-list-version-status.md`](2026-11-25-exam-list-version-status.md)) đã vào `dev`. Kiểu `Exam` đã có `latestVersionId`, `latestVersionStatus` và `publishedVersionNo`.

## Ràng buộc chung

- Nhánh `feat/frontend-redesign` tách từ `dev` (sau khi PR 1 đã merge), PR vào `dev` (Squash).
- **Không tự `git commit`/`git push`/tạo PR.** Mỗi task kết thúc bằng một bước "Điểm dừng": báo người dùng commit. Cuối plan, viết mô tả PR dạng Markdown.
- Trước khi dùng API Next.js (`next/font`, `usePathname`...), đọc tài liệu trong `frontend/node_modules/next/dist/docs/` (theo `frontend/AGENTS.md`). Tài liệu font: `01-app/01-getting-started/13-fonts.md`, `01-app/03-api-reference/02-components/font.md`.
- Static export: không dùng tính năng cần Node server. Route động vẫn qua query string.
- CSP (`scripts/inject-csp.mjs`): `font-src 'self'`, `script-src 'self'` kèm hash. Không tải gì từ CDN, không thêm script inline.
- Mọi chuỗi tiếng Việt nằm trong `src/messages/vi.ts`. Không viết chuỗi tiếng Việt trong component.
- Nội dung câu hỏi và AI chỉ render qua `MathMarkdown`.
- Không thêm dependency mới (không thư viện UI, không thư viện icon).
- **Token** (giá trị lấy nguyên từ spec mục 6.1): board `#2A7470`, on-board `#FFFFFF`, on-board-muted `#DCEDEB`, accent `#2A7470`, accent-strong `#1F5C59`, margin `#EBA9B1`, paper `#FCFCFB`, surface `#FFFFFF`, sidebar `#F4F6F7`, ink `#1C1F24`, muted `#6B6F78`, line `#E4E7EB`, tint `#EEF1F4`.
- **Màu trạng thái** (vạch / chữ / nền nhạt): ok `#2F7D56`/`#2F7D56`/`#EAF4EE`; wait `#E0A82E`/`#8A5F05`/`#FDF5E2`; rej `#C8102E`/`#C8102E`/`#FCEBEE`; draft `#C5C7CD`/`#6B6F78`; pub `#2457A6`/`#2457A6`/`#EAF0FA`; arch `#8E9199`/`#73767E`.
- **Kiểu chữ:** Be Vietnam Pro 400/500/600/700, subset `vietnamese` và `latin` cho giao diện; Noto Serif 400/700 cho trang in. Tiêu đề trang 26px/600, tiêu đề mục 15px/600, chữ thường 14px, chữ phụ 12.5px. Số dùng `tabular-nums`.
- **Câu chữ:** viết hoa đầu câu; không nối thông tin bằng " · "; không thêm "→" sau link; không viết hoa toàn bộ nhãn (trừ tiêu đề tờ đề in).
- Bo góc 6–8px, không đổ bóng dày; dòng danh sách không biến thành thẻ. Chuyển động tối đa 200ms, tắt khi `prefers-reduced-motion: reduce`.
- Phiên bản `context.md` của PR này là **0.26**.

## Trọng tâm review

1. Đường dẫn không có dấu `/` cuối (trong test, hoặc khi người dùng gõ tay) vẫn phải sáng đúng mục menu. `/admin/ai-usage/` sáng "Thống kê AI", không sáng "Quản trị" (Task 4).
2. `localStorage` ném lỗi (chế độ riêng tư, bị chặn): menu vẫn hoạt động, mặc định mở ở màn rộng (Task 4).
3. Đổi kích thước cửa sổ qua mốc 1024px khi menu lớp phủ đang mở: không để lại nền mờ hay bẫy focus (Task 4).
4. Câu hỏi có stem rất dài hoặc chứa công thức KaTeX trong `ListRow`: không tràn ngang ở khổ 390px (Task 3).
5. Trang chủ khi một request lỗi 403/500: chỉ nhóm đó hiện dòng lỗi, các nhóm khác vẫn hiện (Task 11).

## Cấu trúc file

```
frontend/src/
  app/fonts.ts                      Be Vietnam Pro + Noto Serif (next/font)
  app/globals.css                   @theme token, base, print
  app/layout.tsx                    Providers > SidebarProvider > AppShell > AuthGate
  lib/ui/status.ts                  Tone + map status → tone + class theo tone
  components/ui/                    button, notice, panel, tag, status-text, field,
                                    empty-state, list-row, filter-bar, pager,
                                    board-tabs, meta-bar, data-table
  components/shell/                 nav-items.ts, sidebar-state.tsx, side-nav.tsx,
                                    sidebar-toggle.tsx, page-header.tsx, app-shell.tsx
  components/home/                  shortcuts.tsx, todo-section.tsx
  lib/home/todo.ts                  hàm thuần cho "Cần bạn xử lý"
  messages/vi.ts, messages/vi.test.ts
```
Xóa: `components/role-nav.tsx`, `components/role-nav.test.tsx`, `components/user-menu.tsx`.

---

### Task 1: Tài liệu, token và font

**Files:**
- Modify: `docs/context.md` (mục 9.2 thêm "Chi tiết triển khai giao diện"; sửa tên menu trong "Chi tiết triển khai B8" thành "Sinh câu bằng AI"; thêm lịch sử **0.26 — 25/11/2026**; dòng phiên bản đầu file)
- Modify: `.gitignore` (thêm `.superpowers/`)
- Create: `frontend/src/app/fonts.ts`
- Modify: `frontend/src/app/globals.css`, `frontend/src/app/layout.tsx` (chỉ gắn class font lên `<html>` ở task này)

**Interfaces:**
- Produces: `export const sans` (Be_Vietnam_Pro, `variable: "--font-be-vietnam"`) và `export const serif` (Noto_Serif, `variable: "--font-noto-serif"`) trong `fonts.ts`. Lớp Tailwind theo token: `bg-board`, `text-on-board`, `text-on-board-muted`, `bg-accent`, `bg-accent-strong`, `border-margin`, `bg-paper`, `bg-surface`, `bg-sidebar`, `text-ink`, `text-muted`, `border-line`, `bg-tint`, cùng các màu `ok`, `ok-bg`, `wait`, `wait-text`, `wait-bg`, `rej`, `rej-bg`, `draft`, `pub`, `pub-bg`, `arch`, `arch-text`; `font-sans`, `font-serif`.

- [ ] **Step 1:** Viết mục "Chi tiết triển khai giao diện" trong `context.md` mục 9.2 theo spec mục 6.1 đến 6.8: token, font, menu dọc kèm nút đóng/mở và key `exammaker.ui.sidebar`, tấm bảng đầu trang, nguyên tắc câu chữ, mục "Cần bạn xử lý" và nguồn dữ liệu. Thêm lịch sử 0.26.
- [ ] **Step 2:** Tạo `fonts.ts` với hai font như khối Interfaces, `display: "swap"`.
- [ ] **Step 3:** Trong `globals.css`, khai báo token bằng `@theme` (màu) và `@theme inline` (`--font-sans: var(--font-be-vietnam), system-ui, sans-serif; --font-serif: var(--font-noto-serif), "Times New Roman", serif;`). `body` dùng `bg-paper text-ink font-sans antialiased` và cỡ chữ 14px. Thêm quy tắc `:focus-visible` (vòng 2px màu accent, offset 2px) và khối `@media (prefers-reduced-motion: reduce)` tắt transition và animation. Giữ nguyên các quy tắc in hiện có.
- [ ] **Step 4:** Trong `layout.tsx`, gắn `${sans.variable} ${serif.variable}` vào `<html>`. Phần còn lại của layout chưa đổi.
- [ ] **Step 5: Kiểm tra build và CSP**

Run: `cd frontend && npm run build && grep -l "font-src 'self'" out/index.html && ls out/_next/static/media | head`
Expected: build thành công, có file font `.woff2` trong `out/_next/static/media`, CSS không trỏ tới `fonts.googleapis.com` (`grep -r "fonts.googleapis" out/` không ra kết quả).
- [ ] **Step 6:** `npm test && npm run lint && npm run typecheck` đều qua.
- [ ] **Step 7: Điểm dừng.** Gợi ý commit `feat(frontend): add design tokens and self-hosted fonts`.

---

### Task 2: Ánh xạ trạng thái sang màu

**Files:**
- Create: `frontend/src/lib/ui/status.ts`
- Test: `frontend/src/lib/ui/status.test.ts`

**Interfaces:**
- Produces:
  - `export type Tone = "ok" | "wait" | "rej" | "draft" | "pub" | "arch";`
  - `questionTone(s: QuestionStatus): Tone`, `examVersionTone(s: ExamVersionStatus): Tone`, `aiJobTone(s: AiJobStatus): Tone`, `userTone(isActive: boolean): Tone` (các kiểu lấy từ `components["schemas"]`)
  - `export const toneClass: Record<Tone, { stroke: string; text: string; bg: string }>`: chuỗi class Tailwind, ví dụ `stroke: "border-l-ok"`, `text: "text-ok"`, `bg: "bg-ok-bg"`. Với `draft` và `arch`, `bg` là `"bg-tint"`.

- [ ] **Step 1: Viết test**

```ts
it.each([["Draft","draft"],["Submitted","wait"],["Approved","ok"],["Rejected","rej"],["Archived","arch"]])("question %s → %s", (s, t) => expect(questionTone(s)).toBe(t));
it.each([["Draft","draft"],["Submitted","wait"],["Approved","ok"],["Rejected","rej"],["Published","pub"],["Archived","arch"]])("exam version %s → %s", ...);
it.each([["Queued","draft"],["Running","wait"],["Succeeded","ok"],["Failed","rej"]])("ai job %s → %s", ...);
it("user tone", () => { expect(userTone(true)).toBe("ok"); expect(userTone(false)).toBe("arch"); });
it("wait text uses the darker shade", () => expect(toneClass.wait.text).toBe("text-wait-text"));
it("arch text uses the accessible grey", () => expect(toneClass.arch.text).toBe("text-arch-text"));
```
- [ ] **Step 2:** `npx vitest run src/lib/ui/status.test.ts`. Expected: FAIL (module chưa có).
- [ ] **Step 3:** Viết `status.ts` bằng `switch` đầy đủ, có kiểm tra `never` để TypeScript báo lỗi khi enum thêm giá trị mới.
- [ ] **Step 4:** Chạy lại. Expected: PASS.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): map statuses to design tones`.

---

### Task 3: Component trình bày dùng chung

**Files:**
- Create trong `frontend/src/components/ui/`: `button.tsx`, `notice.tsx`, `panel.tsx`, `tag.tsx`, `status-text.tsx`, `field.tsx`, `empty-state.tsx`, `list-row.tsx`, `filter-bar.tsx`, `pager.tsx`, `board-tabs.tsx`, `meta-bar.tsx`, `data-table.tsx`
- Modify: `frontend/src/messages/vi.ts` (thêm `common.pageOf: (page: number, total: number) => string` cho dạng "Trang x/y")
- Test: `frontend/src/components/ui/ui.test.tsx`

**Interfaces:**
- Consumes: `Tone`, `toneClass` (Task 2).
- Produces (mọi component nhận thêm `className?: string`):
  - `Button(props: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "primary" | "secondary" | "danger" | "approve" | "boardPrimary" | "boardSecondary"; size?: "md" | "sm"; disabledReason?: string })`. Mặc định `type="button"`, variant `primary`, size `md`. Khi `disabled` và có `disabledReason` thì đặt `title` và `aria-describedby` trỏ tới một `<span className="sr-only">`.
  - `ButtonLink(props: { href: string; variant?; size?; children: ReactNode })`: bọc `next/link`, cùng kiểu với `Button`.
  - `Notice({ tone: "warn" | "error" | "info" | "success"; title?: ReactNode; children?: ReactNode; action?: ReactNode })`: `error` có `role="alert"`, các tone khác có `role="status"`.
  - `Panel({ title?: ReactNode; aside?: ReactNode; children })`: render `<section>`, có `<h2>` nếu có `title`.
  - `Tag({ tone?: "neutral" | "ai" | "ok" | "rej"; children })`.
  - `StatusText({ tone: Tone; children })`.
  - `Field({ label: ReactNode; htmlFor: string; hint?: ReactNode; error?: string; children })`, cùng `Input`, `Select`, `Textarea` (truyền thẳng mọi prop native kể cả `ref`, dùng được với `register` của react-hook-form) và `Checkbox({ label: ReactNode } & InputHTMLAttributes<HTMLInputElement>)`.
  - `EmptyState({ title: ReactNode; description?: ReactNode; action?: ReactNode; busy?: boolean })`: `busy` hiện vòng quay có `aria-hidden` và `role="status"` trên khối.
  - `ListRow({ tone: Tone; href?: string; title: ReactNode; meta?: ReactNode; reason?: ReactNode; end?: ReactNode; highlight?: "error" })`: có `data-tone={tone}`. Khi có `href`, tiêu đề là `Link` kèm lớp phủ giãn toàn dòng (`after:absolute after:inset-0`), khối `end` nằm trên lớp phủ (`relative z-10`) nên nút bên trong vẫn bấm được. Nội dung có `min-w-0` và `break-words`. Dưới 640px thì khối `end` xuống dòng.
  - `FilterBar({ children })` và `FilterSelect(props: SelectHTMLAttributes<HTMLSelectElement> & { label: string })`: nhãn ẩn bằng `sr-only`, nền tint.
  - `Pager({ page: number; totalPages: number; onPage: (page: number) => void })`: nút trước/sau tự `disabled` ở hai đầu.
  - `BoardTabs({ items: { key: string; label: ReactNode }[]; active: string; onSelect: (key: string) => void })`: `role="tablist"`, mỗi mục là `role="tab"` kèm `aria-selected`.
  - `MetaBar({ items: { label: ReactNode; value: ReactNode }[]; actions?: ReactNode })`.
  - `DataTable({ children })`: bọc `<div className="overflow-x-auto">` và tạo kiểu cho `table/th/td` bên trong. Ô số dùng class `num` (canh phải, `tabular-nums`).

- [ ] **Step 1: Viết test** (`ui.test.tsx`):
  - `Button` disabled kèm `disabledReason="Cần rà soát trước"`: `getByRole("button")` có `disabled`, và `aria-describedby` trỏ tới phần tử có text đó.
  - `ListRow` có `href="/questions/detail/?id=1"` và `end={<button>Thêm</button>}`: có link tên bằng tiêu đề, nút "Thêm" bấm được (`fireEvent.click` gọi handler), có `data-tone`.
  - `ListRow` không có `href`: không có link.
  - `Pager` ở trang 1/3: nút trang trước `disabled`; bấm trang sau gọi `onPage(2)`; hiện `messages.common.pageOf(1, 3)`.
  - `Notice tone="error"` có `role="alert"`.
  - `BoardTabs`: tab đang chọn có `aria-selected="true"`; bấm tab khác gọi `onSelect(key)`.
  - `StatusText tone="wait"` có class `text-wait-text`.
- [ ] **Step 2:** `npx vitest run src/components/ui`. Expected: FAIL.
- [ ] **Step 3:** Viết các component theo khối Interfaces, chỉ dùng token của Task 1.
- [ ] **Step 4:** Chạy lại. Expected: PASS. Chạy thêm `npm run typecheck && npm run lint`.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): add shared UI components`.

---

### Task 4: Khung ứng dụng: menu dọc, nút đóng/mở, tấm bảng đầu trang

**Files:**
- Create trong `frontend/src/components/shell/`: `nav-items.ts`, `sidebar-state.tsx`, `side-nav.tsx`, `sidebar-toggle.tsx`, `page-header.tsx`, `app-shell.tsx`
- Modify: `frontend/src/app/layout.tsx`, `frontend/src/components/auth-gate.tsx` và `frontend/src/app/auth/callback/page.tsx` (các khối thông báo dùng `EmptyState`, logic giữ nguyên), `frontend/src/messages/vi.ts` (`nav.*`, `auth.roleLine`, `shell.toggleMenu`, `shell.navLabel`)
- Delete: `frontend/src/components/role-nav.tsx`, `role-nav.test.tsx`, `user-menu.tsx`
- Modify test: `frontend/src/app/a10-screens.test.tsx` (test "renders role-specific navigation links..." chuyển sang dùng `SideNav`)
- Test: `frontend/src/components/shell/nav-items.test.ts`, `frontend/src/components/shell/shell.test.tsx`

**Interfaces:**
- Consumes: `Button`, `EmptyState` (Task 3); `useAuth`, `isPublicPath` (`src/lib/auth/auth-context`).
- Produces:
  - `type NavKey = "home" | "questions" | "ai" | "exams" | "review" | "aiUsage" | "admin";`, `type NavItem = { key: NavKey; href: string; label: string };`
  - `navItemsFor(role: Role): NavItem[]`. Thứ tự và phạm vi: teacher `home, questions, ai, exams`; head thêm `review`; admin `home, questions, exams, review, aiUsage, admin`. Href lần lượt: `/`, `/questions/`, `/ai/`, `/exams/`, `/review/`, `/admin/ai-usage/`, `/admin/`.
  - `activeNavKey(items: NavItem[], pathname: string): NavKey | null`: chuẩn hóa thêm `/` cuối; `home` chỉ khớp đúng `/`; các mục khác lấy tiền tố khớp dài nhất.
  - `SidebarProvider({ children })` và `useSidebar(): { open: boolean; narrow: boolean; toggle(): void; close(): void }`. `narrow` là `!matchMedia("(min-width: 1024px)").matches`, theo dõi sự kiện `change`. Màn rộng: đọc/ghi `localStorage["exammaker.ui.sidebar"]` (`"open"`/`"closed"`), lỗi thì mặc định mở. Màn hẹp: mặc định đóng, không ghi storage. Chuyển từ hẹp sang rộng: trả về giá trị đã lưu.
  - `SidebarToggle()`: nút `id="app-sidebar-toggle"`, `aria-controls="app-sidebar"`, `aria-expanded={open}`, `aria-label={vi.shell.toggleMenu}`, nội dung là biểu tượng ☰ (SVG inline 3 vạch).
  - `SideNav()`: `<nav id="app-sidebar" aria-label={vi.shell.navLabel}>`, gồm tên app, các mục (`aria-current="page"` cho mục đang sáng), khối người dùng (họ tên, `vi.auth.roleLine(role, departmentName)`, nút đăng xuất gọi `logout`). Màn rộng mà `open=false` thì không render. Màn hẹp mà `open=true` thì render dạng lớp phủ `fixed`, kèm nút nền mờ để đóng; khi mở thì focus vào mục đầu, Esc hoặc bấm nền mờ thì đóng và trả focus về `#app-sidebar-toggle`; đổi pathname thì đóng. Có `print:hidden`.
  - `PageHeader({ title: ReactNode; description?: ReactNode; crumb?: { href: string; label: string }; actions?: ReactNode; tabs?: ReactNode })`: tấm bảng `bg-board text-on-board`, viền dưới 4px `border-margin`, `SidebarToggle` ở đầu, `<h1>`, có `print:hidden`.
  - `AppShell({ children })`: khi `status === "signedIn"`, có `me` và không phải trang công khai thì render `flex` gồm `SideNav` và `<main className="min-w-0 flex-1">`; ngược lại chỉ render `<main>`.
  - `layout.tsx`: `Providers > SidebarProvider > AppShell > AuthGate > children`. Bỏ header ngang và giới hạn `max-w-5xl`.

- [ ] **Step 1: Viết test `nav-items.test.ts`**
  - `navItemsFor("teacher").map(i => i.key)` bằng `["home","questions","ai","exams"]`; head bằng `[...,"review"]`; admin bằng `["home","questions","exams","review","aiUsage","admin"]`.
  - `activeNavKey(admin, "/admin/ai-usage")` là `"aiUsage"`; `"/admin/"` là `"admin"`; `"/questions/detail"` là `"questions"`; `"/"` là `"home"`; `"/exams/print/"` là `"exams"`; `"/unknown/"` là `null`.
- [ ] **Step 2: Viết test `shell.test.tsx`** (mock `useAuth`, `usePathname`, `window.matchMedia`, `localStorage`):
  - Màn rộng, storage trống: menu hiện, `#app-sidebar-toggle` có `aria-expanded="true"`. Bấm nút thì menu ẩn và `localStorage.getItem("exammaker.ui.sidebar")` là `"closed"`. Render lại thì vẫn đóng.
  - `localStorage.getItem` ném lỗi: không crash, menu mở.
  - Màn hẹp: mặc định đóng. Bấm nút thì mở; `keyDown Escape` thì đóng và focus về nút; storage không bị ghi.
  - Đổi `usePathname` khi đang mở ở màn hẹp thì menu đóng.
  - Teacher không thấy link `messages.nav.review`; head thấy; admin thấy `messages.nav.aiUsage` và không thấy `messages.nav.ai`.
  - Mục đang sáng có `aria-current="page"`.
  - `AppShell` với `status: "signedOut"`: không có `nav#app-sidebar`.
- [ ] **Step 3:** Chạy `npx vitest run src/components/shell`. Expected: FAIL.
- [ ] **Step 4:** Viết các file shell theo Interfaces. Cập nhật `vi.ts`: `nav.ai = "Sinh câu bằng AI"`, `nav.review = "Duyệt câu và đề"`, thêm `auth.roleLine(role, dept)` trả `"{vai trò}, tổ {dept}"` (`dept` null thì chỉ có vai trò), `shell.toggleMenu = "Đóng hoặc mở menu"`, `shell.navLabel = "Menu chính"`. Sửa `layout.tsx` và `auth-gate.tsx`. Xóa ba file cũ. Chuyển test nav trong `a10-screens.test.tsx` sang `SideNav`.
- [ ] **Step 5:** Chạy lại cùng `npm test && npm run typecheck && npm run lint`. Expected: PASS.
- [ ] **Step 6: Điểm dừng.** Gợi ý commit `feat(frontend): add collapsible side navigation and page header`.

---

### Task 5: Viết lại câu chữ

**Files:**
- Modify: `frontend/src/messages/vi.ts`; các nơi gọi hàm có chữ ký đổi (`components/ai/generate-form.tsx`, `app/ai/job/page.tsx`, `app/admin/ai-usage/page.tsx`); test liên quan (`app/ai/page.test.tsx`, `app/ai/job/page.test.tsx`, `app/admin/ai-usage/page.test.tsx`)
- Test: `frontend/src/messages/vi.test.ts`

**Interfaces:**
- Produces (chữ ký mới):
  - `ai.quota(jobsLeft: number, jobsLimit: number, tokensLeft: string, tokensLimit: string, resetsAt: string)`. Kết quả: `` `Hôm nay còn ${jobsLeft}/${jobsLimit} lượt và ${tokensLeft}/${tokensLimit} token. Làm mới lúc ${resetsAt}.` `` Người gọi tự tính số còn lại, không âm.
  - `ai.summary(n: number, dropped: number)`: `` `AI tạo ${n} câu` `` cộng thêm `` `, bỏ ${dropped} câu trùng với ngân hàng.` `` khi `dropped > 0`, ngược lại cộng `"."`.
  - `ai.reviewProgress(reviewed: number, n: number)`: `` `Đã rà soát ${reviewed}/${n}` ``.
  - `aiUsage.estimateNotice = "Chi phí là ước tính theo bảng giá cấu hình, không phải hóa đơn AWS."`; `aiUsage.cost = "Chi phí ước tính"`; `aiUsage.totals(jobs, tokens, cost)`: `` `Tổng theo bộ lọc: ${jobs} job, ${tokens} token, khoảng ${cost}` ``.
  - Chuỗi `home.*` để sang Task 11, cùng lúc với trang chủ mới.

- [ ] **Step 1: Viết test `vi.test.ts`**: duyệt đệ quy mọi giá trị trong `vi`. Chuỗi được kiểm trực tiếp. Hàm được gọi với tham số mẫu (số `1` cho tham số số, `"x"` cho chuỗi, dựa trên `fn.length`; với `auth.roleLine` dùng `("teacher","Toán")`). Mảng được duyệt từng phần tử. Assert không giá trị nào chứa `" · "` hoặc `"→"`. Thêm assert cụ thể cho `ai.quota(7,10,"64.200","100.000","07:00")` bằng `"Hôm nay còn 7/10 lượt và 64.200/100.000 token. Làm mới lúc 07:00."`, và `ai.summary(5,0)` bằng `"AI tạo 5 câu."`.
- [ ] **Step 2:** Chạy, xác nhận FAIL (các chuỗi đang có "·").
- [ ] **Step 3:** Sửa `vi.ts` theo Interfaces. Rà toàn bộ file theo nguyên tắc ở spec mục 6.7: nút nói đúng việc nó làm; thông báo thành công dùng cùng động từ; lỗi nói rõ chuyện gì xảy ra và cách xử lý, không xin lỗi; trang trống mời hành động. Giữ nguyên tên trạng thái và **giữ nguyên nghĩa** của `ai.noteWarning`. Sửa nơi gọi và test theo chữ ký mới.
- [ ] **Step 4:** `npm test && npm run typecheck`. Expected: PASS.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): rewrite interface copy`.

---

### Task 6: Ngân hàng câu hỏi và chi tiết câu hỏi

**Files:**
- Modify: `frontend/src/app/questions/page.tsx`, `frontend/src/app/questions/detail/page.tsx`, `frontend/src/components/questions/question-payload-view.tsx`
- Test: `frontend/src/app/a10-screens.test.tsx` (các test của hai trang này), thêm test mới cùng file

**Interfaces:**
- Consumes: `PageHeader`, `ListRow`, `FilterBar`, `FilterSelect`, `Checkbox`, `Pager`, `Panel`, `Field`/`Input`/`Select`/`Textarea`, `Notice`, `Tag`, `StatusText`, `Button`, `ButtonLink`, `EmptyState`, `questionTone`.

- [ ] **Step 1: Thêm test**
  - Danh sách: câu `Rejected` có `reviewNote` hiện dòng lý do trong `ListRow` có `data-tone="rej"`; câu AI có tag `messages.questions.aiFlags.Disagree` khi `aiVerification === "Disagree"`.
  - Chi tiết: câu `Rejected` hiện `Notice` có `role="alert"` chứa `reviewNote` và nằm trước form (so thứ tự bằng `compareDocumentPosition`); câu AI chưa rà soát thì nút `messages.questions.submit` bị `disabled` và có mô tả bằng `messages.questions.aiReviewRequiredNotice`.
- [ ] **Step 2:** Chạy `npx vitest run src/app/a10-screens.test.tsx`. Expected: các test mới FAIL.
- [ ] **Step 3:** Dựng lại hai trang theo mockup (màn "Ngân hàng câu hỏi" và "Chi tiết / sửa câu hỏi") và spec mục 6.5. Danh sách: `PageHeader` (nút "Tạo câu hỏi" là `boardPrimary`, chỉ teacher/head), `FilterBar`, `ListRow` (tiêu đề là stem qua `MathMarkdown` giới hạn 2 dòng; meta dạng "Dạng, mức độ. Chương"), `Pager`, `EmptyState` khi rỗng. Chi tiết: hai cột `lg:grid-cols-[minmax(0,1fr)_320px]`, form bên trái, cột phải có ba `Panel` (Thông tin, Kiểm chứng AI, Rà soát của tác giả). Nút chuyển trạng thái đặt trên tấm bảng. "Xóa" là `danger`, tách khỏi "Lưu". Không đổi logic query/mutation.
- [ ] **Step 4:** Chạy lại test của file cùng `npm run typecheck`. Expected: PASS.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): restyle question bank screens`.

---

### Task 7: Sinh câu bằng AI và kết quả job

**Files:**
- Modify: `frontend/src/app/ai/page.tsx`, `frontend/src/app/ai/job/page.tsx`, `frontend/src/components/ai/generate-form.tsx`, `job-list.tsx`, `job-status-panel.tsx`, `generated-question-card.tsx`
- Test: `frontend/src/app/ai/page.test.tsx`, `frontend/src/app/ai/job/page.test.tsx`

**Interfaces:**
- Consumes: shell, các component ui, `aiJobTone`, `ai.quota`, `ai.summary`, `ai.reviewProgress` (Task 5).

- [ ] **Step 1: Thêm test**
  - `/ai/`: dòng mô tả trên tấm bảng bằng `messages.ai.quota(...)` với số còn lại tính từ quota mock; cảnh báo `messages.ai.noteWarning` luôn hiện; job `Running` có `data-tone="wait"`.
  - `/ai/job/`: job `Succeeded` hiện `messages.ai.summary(n, dropped)` và `messages.ai.reviewProgress(r, n)`; câu có `aiVerification: "Disagree"` hiện tag `messages.ai.disagreeWarning`.
- [ ] **Step 2:** Chạy hai file test. Expected: các test mới FAIL.
- [ ] **Step 3:** Dựng lại theo mockup (màn "Sinh câu bằng AI" và "Kết quả job AI"). `/ai/`: hai cột (form trong `Panel`, "Job của tôi" là các `ListRow`), job `Running` có chấm nhấp nháy `motion-safe:animate-pulse`. `/ai/job/`: `PageHeader` có crumb về `/ai/`, tiêu đề là môn, khối, chương, nút "Sinh lại với cùng yêu cầu"; dải `Notice` theo trạng thái job; mỗi câu là một khối "Câu n", đáp án đúng nền `bg-ok-bg`, câu `Disagree` có viền và vạch đỏ. Giữ nguyên mọi trạng thái của B8 (không khả dụng, lỗi tải, bản cũ, gợi ý chờ lâu). Dòng job **không** hiện tiến độ rà soát.
- [ ] **Step 4:** Chạy lại cùng `npm run typecheck`. Expected: PASS.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): restyle AI generation screens`.

---

### Task 8: Đề thi: danh sách, soạn đề, bản in

**Files:**
- Modify: `frontend/src/app/exams/page.tsx`, `frontend/src/app/exams/view/page.tsx`, `frontend/src/app/exams/print/page.tsx`, `frontend/src/components/exams/*.tsx`, `frontend/src/components/exams/print/*.tsx`, `frontend/src/app/globals.css` (phần in)
- Test: `frontend/src/app/exams/page.test.tsx`, `frontend/src/app/exams/view/page.test.tsx`, `frontend/src/app/exams/print/page.test.tsx`, `frontend/src/components/exams/exam-composer.test.tsx`

**Interfaces:**
- Consumes: shell, ui, `examVersionTone`; các trường `Exam.latestVersionId`, `latestVersionStatus` và `publishedVersionNo` (PR 1).
- Produces: `vi.exams.latestVersion(no: number, status: string)` trả `` `v${no}, ${status}` ``; `vi.exams.publishedVersion(no: number)` trả `` `Đang xuất bản v${no}` ``.

- [ ] **Step 1: Thêm test**
  - `/exams/`: đề có `latestVersionStatus: "Rejected"` thì `ListRow` có `data-tone="rej"` và text `messages.exams.latestVersion(3, messages.review.examStatuses.Rejected)`; đề có `publishedVersionNo: 2` thì hiện `messages.exams.publishedVersion(2)`.
  - Soạn đề: câu nằm trong `invalidQuestionIds` có `data-invalid="true"` (giữ test hiện có, đổi selector nếu cần).
  - Trang in chế độ học sinh: phần tử gốc của tờ đề có class `font-serif`; không render `nav#app-sidebar` bên trong vùng tờ đề.
- [ ] **Step 2:** Chạy bốn file test. Expected: các test mới FAIL.
- [ ] **Step 3:** Dựng lại theo mockup (màn "Danh sách đề thi", "Soạn đề (hai cột)", "Bản in đề / đáp án") và spec mục 6.5. Danh sách dùng trường mới từ PR 1. Soạn đề: tấm bảng gồm crumb, tiêu đề kèm "Sửa tiêu đề", chọn phiên bản, "Có thay đổi chưa lưu" và các nút; `MetaBar`; hai cột `xl:grid-cols-2`; câu chưa duyệt có viền đỏ; câu nguồn đổi revision có dòng chữ `text-wait-text`. Bản in: thanh công cụ trắng có nút chuyển chế độ và "In / Lưu PDF"; tờ A4 `font-serif`, chỉ đen trắng; khi in ẩn menu, tấm bảng và thanh công cụ (`print:hidden`), giữ `break-inside: avoid`.
- [ ] **Step 4:** Chạy lại cùng `npm run typecheck`. Expected: PASS.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): restyle exam list, composer and print view`.

---

### Task 9: Duyệt: hàng đợi, duyệt câu, duyệt đề

**Files:**
- Modify: `frontend/src/app/review/page.tsx`, `frontend/src/app/review/question/page.tsx`, `frontend/src/app/review/exam/page.tsx`
- Test: `frontend/src/app/a10-screens.test.tsx` (các test review)

**Interfaces:**
- Consumes: shell (`PageHeader` có `tabs`), `BoardTabs`, `ListRow`, `Panel`, `Checkbox`, `MetaBar`, `Notice`, `Button` (variant `approve` và `danger`).
- Produces: `vi.review.tabWithCount(label: string, count: number)` trả `` `${label} (${count})` ``.

- [ ] **Step 1: Thêm test**
  - Hàng đợi: tab câu hỏi hiện `messages.review.tabWithCount(messages.review.questionsTab, totalItems)` với `role="tab"` và `aria-selected="true"`.
  - Duyệt câu: khi chưa tick đủ checklist thì nút `messages.review.approve` bị `disabled` và có mô tả `messages.review.checklistRequired`; tick đủ thì bật.
- [ ] **Step 2:** Chạy test. Expected: các test mới FAIL.
- [ ] **Step 3:** Dựng lại theo mockup (màn "Hàng đợi duyệt", "Duyệt câu hỏi", "Duyệt và xuất bản đề"). Duyệt câu: hai cột, cột phải là `Panel` dính khi cuộn (`lg:sticky lg:top-4`) gồm checklist, nhận xét, "Duyệt" (`approve`) và "Từ chối" (`danger`). Duyệt đề: `MetaBar` có trạng thái; cảnh báo nguồn là `Notice warn`; nút Xuất bản, Thu hồi, Lưu trữ trên tấm bảng theo quyền hiện có.
- [ ] **Step 4:** Chạy lại cùng `npm run typecheck`. Expected: PASS.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): restyle review screens`.

---

### Task 10: Quản trị và thống kê AI

**Files:**
- Modify: `frontend/src/app/admin/page.tsx`, `frontend/src/app/admin/ai-usage/page.tsx`
- Test: `frontend/src/app/a10-screens.test.tsx` (các test admin), `frontend/src/app/admin/ai-usage/page.test.tsx`

**Interfaces:**
- Consumes: shell, `BoardTabs`, `DataTable`, `StatusText`, `userTone`, `Button`, `Field`.
- Produces: `vi.admin.totalItems(n: number)` trả `` `${n} mục` ``.

- [ ] **Step 1: Thêm test**
  - Quản trị: tab Người dùng có `aria-selected="true"`; user bị khóa hiện `StatusText` có class `text-arch-text`; dòng mô tả trên tấm bảng bằng `messages.admin.totalItems(totalItems)` của tab đang mở.
  - Thống kê AI: hàng số tổng có 4 ô, ô chi phí chứa giá trị định dạng; vẫn hiện `messages.aiUsage.estimateNotice`.
- [ ] **Step 2:** Chạy test. Expected: các test mới FAIL.
- [ ] **Step 3:** Dựng lại theo mockup (màn "Quản trị người dùng" và "Thống kê AI"). Tab trên tấm bảng; "Đối soát Cognito" (`boardSecondary`) và "Tạo tài khoản" (`boardPrimary`) trên tấm bảng; bảng dùng `DataTable`; nút Khóa là `danger`. Thống kê: bộ lọc, hàng 4 số tổng (số dùng `tabular-nums`), ghi chú ước tính, `DataTable`. Không gọi thêm request đếm.
- [ ] **Step 4:** Chạy lại cùng `npm run typecheck`. Expected: PASS.
- [ ] **Step 5: Điểm dừng.** Gợi ý commit `feat(frontend): restyle admin and AI usage screens`.

---

### Task 11: Trang chủ: lối tắt và "Cần bạn xử lý"

**Files:**
- Create: `frontend/src/lib/home/todo.ts`, `frontend/src/components/home/shortcuts.tsx`, `frontend/src/components/home/todo-section.tsx`
- Modify: `frontend/src/app/page.tsx`, `frontend/src/messages/vi.ts` (`home.*`)
- Test: `frontend/src/lib/home/todo.test.ts`, `frontend/src/app/page.test.tsx`

**Interfaces:**
- Consumes: `navItemsFor` (Task 4), `ListRow`, `Notice`, `EmptyState`, `api` (`src/lib/api/client`), các kiểu `Question`, `AiJob`, `Exam`.
- Produces:
  - `groupUnreviewedAiQuestions(items: Question[]): { aiJobId: string; count: number; subjectId: string; topicId: string; grade: number; firstCreatedAt: string }[]`: chỉ lấy `source === "AI" && !aiReviewedByAuthor && aiJobId`, gom theo `aiJobId`, sắp `firstCreatedAt` giảm dần.
  - `attentionJobs(jobs: AiJob[], now: Date): AiJob[]`: lấy `Queued` và `Running`, cùng `Failed` có `finishedAt` trong 24 giờ trước `now`.
  - `rejectedExams(exams: Exam[]): Exam[]`: lọc `latestVersionStatus === "Rejected"`.
  - `TodoSection({ role: Role })`: các nhóm và request theo spec mục 6.6, mỗi nhóm một query key `["home", "<group>"]`, `refetchOnMount: "always"`. Mỗi nhóm tối đa 5 dòng kèm link "Xem tất cả". Nhóm rỗng thì ẩn; mọi nhóm rỗng thì hiện `vi.home.nothingToDo`. Một nhóm lỗi thì hiện `Notice error` nhỏ kèm "Thử lại" (`refetch`), không chặn nhóm khác. Nhóm của head/admin hiện tổng từ `totalItems`.
  - `Shortcuts({ role: Role })`: các mục `navItemsFor(role)` trừ `home`, mỗi mục có tên và `vi.home.shortcutDescriptions[key]`.
  - Chuỗi trong `vi.home` (thay toàn bộ khối cũ, xóa `home.title`): `greeting(name)` trả `` `Xin chào, ${name}` ``; `description = "Soạn câu hỏi, gửi tổ duyệt và ghép đề thi từ những câu đã duyệt."`; `shortcutDescriptions: Record<Exclude<NavKey, "home">, string>` (câu mô tả lấy theo mockup); `todoTitle = "Cần bạn xử lý"`; `nothingToDo = "Không có việc gì cần xử lý."`; `viewAll = "Xem tất cả"`; tiêu đề các nhóm (`rejectedQuestions`, `unreviewedAi`, `aiJobs`, `rejectedExams`, `reviewQuestions`, `reviewExams`); `unreviewedCount(n)` trả `` `Còn ${n} câu chưa rà soát` ``; `rejectedExamRow(title, no)` trả `` `${title}, phiên bản v${no}` ``; `rejectedExamHint = "Tổ trưởng đã từ chối, mở để xem lý do"`; `aiJobFallback(date)` trả `` `Job sinh câu ngày ${date}` ``; `loadFailed = "Không tải được mục này."`.

- [ ] **Step 1: Viết test hàm thuần (`todo.test.ts`)**
  - Ba câu AI chưa rà soát thuộc hai job, cộng một câu Manual và một câu AI đã rà soát: kết quả có 2 nhóm, đếm `[2, 1]`, đúng thứ tự thời gian.
  - `attentionJobs`: `Failed` cách 23 giờ được giữ, cách 25 giờ bị bỏ, `Succeeded` bị bỏ.
  - `rejectedExams` chỉ giữ `Rejected`.
- [ ] **Step 2: Viết test trang (`page.test.tsx`, mock `api.GET` theo path)**
  - Teacher: thấy nhóm câu bị từ chối và nhóm phiên bản đề bị từ chối, không thấy nhóm "chờ bạn duyệt". Head: thấy thêm nhóm chờ duyệt. Admin: chỉ có các nhóm chờ duyệt, không gọi `/questions` hay `/ai/jobs`.
  - Mọi nhóm rỗng: hiện `messages.home.nothingToDo`.
  - `/ai/jobs` trả lỗi 500: nhóm job hiện dòng lỗi kèm nút "Thử lại", nhóm câu bị từ chối vẫn hiện dòng của nó.
  - Tiêu đề là `messages.home.greeting(me.fullName)`.
- [ ] **Step 3:** Chạy hai file. Expected: FAIL.
- [ ] **Step 4:** Viết `todo.ts`, `shortcuts.tsx`, `todo-section.tsx` và trang chủ theo mockup (màn "Trang chủ") và spec mục 6.5 và 6.6. Dòng nhóm AI lấy tên môn và chương từ cache catalog (`["catalog", ...]` qua `fetchAllSubjects`/`fetchAllTopics`). Nếu chưa có cache thì dùng `vi.home.aiJobFallback(date)`.
- [ ] **Step 5:** Chạy lại cùng `npm test && npm run typecheck`. Expected: PASS.
- [ ] **Step 6: Điểm dừng.** Gợi ý commit `feat(frontend): add home shortcuts and attention list`.

---

### Task 12: Kiểm tra cuối và bàn giao

**Files:**
- Không sửa code, trừ khi kiểm tra phát hiện lỗi.

- [ ] **Step 1: Toàn bộ kiểm tra tự động**

Run: `cd frontend && npm run lint && npm run typecheck && npm test && npm run api:check && npm run build`
Expected: tất cả qua; `inject-csp` không báo lỗi.
- [ ] **Step 2: Kiểm tra CSP và font trên bản build**

Phục vụ thư mục `out/` cục bộ (ví dụ `npx serve out`). Mở trang bằng trình duyệt: console không có lỗi CSP, font tải từ `/_next/static/media`.
- [ ] **Step 3: So với mockup**

Chạy `npm run dev` với tài khoản thật hoặc dev. Chụp màn hình từng trang ở 1440px và 390px rồi so với mockup. Kiểm tra: đóng/mở menu và nhớ trạng thái sau khi tải lại; menu lớp phủ ở 390px; điều hướng toàn bộ bằng phím Tab (focus luôn nhìn thấy); tương phản các cặp ở spec mục 6.8 (dùng DevTools).
- [ ] **Step 4: Kiểm tra in**

Mở `/exams/print/` ở cả hai chế độ, xem hộp in của trình duyệt: không có màu app hay menu, mỗi câu không bị cắt giữa trang.
- [ ] **Step 5: Viết mô tả PR**

Tiếng Việt, tiêu đề tiếng Anh `feat(frontend): redesign UI with teal board theme and side navigation`, lưu trong thư mục scratchpad. Nội dung gồm: tóm tắt, ảnh chụp (để người dùng tự chèn), danh sách thay đổi theo task, những chỗ khác mockup (spec mục 9), cách kiểm tra, hướng dẫn nhập trang vào Figma bằng html.to.design (spec mục 6.9), và dòng `🤖 Generated with [Claude Code](https://claude.com/claude-code)` ở cuối. Báo đường dẫn cho người dùng.
