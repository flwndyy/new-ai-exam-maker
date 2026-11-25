# Làm mới giao diện frontend: thiết kế

> Áp dụng cho toàn bộ `/frontend`. Ngữ cảnh: `docs/context.md` mục 9.2 (frontend static export, B2, B8, B9, B10). Mockup tương tác đã duyệt: [`assets/2026-11-25-frontend-redesign-mockup.html`](assets/2026-11-25-frontend-redesign-mockup.html). Mở bằng trình duyệt; bảng màu "Xanh ngọc" là bản đã chọn.

## 1. Mục đích

Giao diện hiện tại dùng Tailwind mặc định (slate, nền trắng, menu chữ nằm ngang) và bị người dùng đánh giá là xấu. Mục tiêu là làm giao diện **đẹp, có thẩm mỹ và đồng bộ** trên cả 14 trang, nhưng **vẫn đơn giản khi dùng**: luồng thao tác, quyền và hành vi giữ nguyên. Sau khi code xong, người dùng nhập các trang thật vào Figma bằng plugin html.to.design để có file Figma cho báo cáo.

## 2. Mục tiêu / không làm / phản mục tiêu

**Mục tiêu**
- Một bộ nhận diện thống nhất: token màu, kiểu chữ, khoảng cách, và các component dùng chung (mục 6.1 đến 6.4).
- Khung ứng dụng mới: menu dọc bên trái có nút đóng/mở, phần đầu trang là "tấm bảng" màu xanh ngọc (mục 6.3).
- Áp lên toàn bộ trang trong `src/app` và component trong `src/components`.
- Trang chủ có mục "Cần bạn xử lý" theo vai trò (mục 6.6).
- Viết lại câu chữ giao diện cho gọn và dễ hiểu (mục 6.7).
- Đạt mức tối thiểu về truy cập: tương phản chữ WCAG AA, focus bàn phím nhìn thấy, tôn trọng `prefers-reduced-motion`, dùng được ở khổ 390px.
- Mở rộng nhỏ ở backend: danh sách đề trả thêm trạng thái phiên bản mới nhất và số phiên bản đang xuất bản (mục 6.11). Làm thành **PR riêng, xong trước** phần frontend.

**Không làm**
- Không đổi luồng nghiệp vụ, quyền, state machine, API client, cơ chế đăng nhập hay polling. Ngoại lệ duy nhất là mở rộng `Exam` ở mục 6.11: chỉ thêm trường đọc, không đổi quyền.
- Không làm dark mode.
- Không dựng file Figma bằng tay hay bằng Figma AI. File Figma lấy từ trang thật qua html.to.design (mục 6.9).
- Không thêm thư viện UI (shadcn, MUI...). Dùng Tailwind v4 có sẵn cùng component tự viết.

**Phản mục tiêu** (làm được nhưng vẫn là thất bại)
- Test hiện có hỏng vì đổi cấu trúc mà không phải vì đổi câu chữ có chủ đích.
- Trạng thái chỉ thể hiện bằng màu, không có chữ đi kèm.
- Trang in đề mang màu hoặc chrome của giao diện app.
- Câu chữ tiếng Việt nằm rải trong component thay vì trong `src/messages/vi.ts`.
- Vi phạm CSP hiện tại (tải font hay script từ CDN lúc chạy).
- Giao diện trông như template chung: thẻ bo góc giống hệt nhau khắp nơi, nhãn chữ in hoa trên mọi tiêu đề, chuỗi thông tin nối bằng dấu "·".

## 3. Quyết định đã chốt với người dùng (25/11/2026)

1. **Cách làm: thiết kế thẳng trong code, rồi nhập sang Figma** (phương án B + C ở mục 4).
2. **Phong cách**: lấy cách trình bày của hướng "Bảng lớp": phần đầu trang là tấm bảng màu, mỗi dòng danh sách có vạch màu bên trái theo trạng thái. Trạng thái **chỉ dùng vạch màu và chữ**, không dùng con dấu hay chữ viết tay.
3. **Màu tấm bảng: xanh ngọc** `#2A7470`. Người dùng đã xem và loại tím, đá phiến (quá tối), xanh mực, các bảng nền sáng và nền trắng.
4. **Menu dọc bên trái** thay cho menu ngang, kèm **một nút đóng/mở** menu.
5. **Giữ mục "Cần bạn xử lý"** trên trang chủ.
6. **Hiện lý do từ chối ở đầu trang chi tiết** câu hỏi và đề. Nút bị khóa phải có lời giải thích.
7. **Viết lại câu chữ** cho gọn và dễ hiểu.
8. Tên hiển thị của app giữ là **"AI Exam Maker"**.
9. **Mở rộng API danh sách đề** để có trạng thái phiên bản mới nhất và số phiên bản đang xuất bản (phương án (a) ở mục 9), làm thành PR backend riêng trước frontend. Đã loại hai phương án: (b) frontend gọi thêm request cho từng đề, và (c) không hiện trạng thái.

## 4. Phương án đã cân nhắc

**Công cụ thiết kế**
- A. Figma AI làm chính (Claude viết prompt, người dùng sinh màn trong Figma, sau đó code theo). Loại: phải chuyển đổi hai lần, và Figma AI khó giữ đồng bộ qua 14 màn.
- **B. Thiết kế thẳng trong code (đã chọn)**: dựng mockup HTML để duyệt, sau đó dựng token và component dùng chung rồi áp lên mọi trang. Đồng bộ tốt, không mất mát khi chuyển sang code, giữ được logic và test.
- **C. Nhập trang thật vào Figma bằng html.to.design (đã chọn, đi kèm B)**: có file Figma để chỉnh tiếp hoặc chụp cho báo cáo.

**Phong cách** (theo skill `frontend-design`: lấy chất liệu từ chính thế giới của giáo viên THPT)
- "Vở ô li, mực tím": giấy kẻ, lề đỏ, con dấu tổ, lời phê viết tay. Người dùng thích màu nhưng sau đó bỏ tím.
- **"Bảng lớp" (cách trình bày được chọn)**: tấm bảng đầu trang, vạch màu theo trạng thái.
- "Tờ giấy thi": tiêu đề kiểu văn bản, ô "Điểm / Lời phê". Không chọn.
- Trong cùng cách trình bày "Bảng lớp" đã thử các màu: tím, xanh bảng, xanh mực, đá phiến, xanh trời nhạt, xanh lá nhạt, nền trắng. **Xanh ngọc được chọn.**

**Bố cục menu**: ngang trên tấm bảng (đã dựng, người dùng thấy dàn trải) và **dọc bên trái có nút đóng/mở (đã chọn)**.

## 5. Ràng buộc

- **Static export** (`output: 'export'`, `trailingSlash: true`): không dùng tính năng cần Node server. Route động vẫn qua query string như hiện tại.
- **Next.js trong repo khác bản quen thuộc**: theo `frontend/AGENTS.md`, đọc tài liệu trong `node_modules/next/dist/docs/` trước khi dùng API như `next/font`.
- **CSP** (`scripts/inject-csp.mjs`): `font-src 'self'`, `style-src 'self' 'unsafe-inline'`, `script-src 'self'` kèm hash. Font phải tự host. `next/font/google` tải font lúc build rồi phục vụ từ `/_next/static`, nên dùng được; cần xác nhận lại sau khi build. Không thêm script inline mới; nếu bắt buộc phải có thì phải được `inject-csp.mjs` tính hash.
- **Câu chữ**: mọi chuỗi tiếng Việt nằm trong `src/messages/vi.ts`. Component không chứa chuỗi tiếng Việt trực tiếp.
- **Test**: có 29 file test (vitest + Testing Library). Một số test so sánh chuỗi viết thẳng hoặc chuỗi lấy từ `vi.*`. Đổi câu chữ phải sửa test tương ứng trong cùng thay đổi.
- **Nội dung AI và KaTeX**: vẫn chỉ hiển thị qua `MathMarkdown` (có sanitize). Không đổi cách render.
- **Trang in** (`/exams/print/`): khi in phải ẩn mọi chrome. Khổ A4, giữ `break-inside: avoid` cho từng câu.
- **API**: dữ liệu cho giao diện chỉ lấy từ API hiện có, cộng với phần mở rộng `Exam` ở mục 6.11.
- **Quy trình repo**: đổi API thì sửa `docs/context.md` và `docs/api/openapi.yaml` trước, rồi tới code. Sau đó chạy `npm run api:generate` để sinh lại `schema.d.ts`, và `npm run api:check` phải qua. Nhánh tách từ `dev`, mở PR vào `dev`.

## 6. Thiết kế

### 6.1 Token màu

Khai báo bằng `@theme` của Tailwind v4 trong `globals.css`, đặt tên theo vai trò, không theo màu.

| Token | Giá trị | Dùng cho |
|---|---|---|
| `board` | `#2A7470` | Nền tấm bảng đầu trang |
| `on-board` | `#FFFFFF` | Chữ chính trên tấm bảng |
| `on-board-muted` | `#DCEDEB` | Chữ phụ trên tấm bảng (tương phản khoảng 4,6:1) |
| `accent` | `#2A7470` | Nút chính trên nền giấy, mục menu đang chọn, checkbox, focus |
| `accent-strong` | `#1F5C59` | Hover và nhấn của `accent`, chữ trên nút trắng ở tấm bảng |
| `margin` | `#EBA9B1` | Đường viền 4px dưới tấm bảng (màu lề đỏ của vở) |
| `paper` | `#FCFCFB` | Nền vùng nội dung |
| `surface` | `#FFFFFF` | Dòng danh sách, panel, ô nhập |
| `sidebar` | `#F4F6F7` | Nền menu dọc |
| `ink` | `#1C1F24` | Chữ chính |
| `muted` | `#6B6F78` | Chữ phụ |
| `line` | `#E4E7EB` | Đường viền, đường kẻ |
| `tint` | `#EEF1F4` | Nền của bộ lọc và tag |

**Màu trạng thái** (cố định, không đổi theo màu bảng, luôn đi kèm chữ):

| Nhóm | Vạch trái | Chữ | Nền nhạt (notice, tag) | Trạng thái dùng |
|---|---|---|---|---|
| ok | `#2F7D56` | `#2F7D56` | `#EAF4EE` | Câu/đề `Approved`, job `Succeeded`, user hoạt động |
| wait | `#E0A82E` | `#8A5F05` | `#FDF5E2` | `Submitted`, job `Running` |
| rej | `#C8102E` | `#C8102E` | `#FCEBEE` | `Rejected`, job `Failed`, lỗi |
| draft | `#C5C7CD` | `#6B6F78` | (không có) | `Draft`, job `Queued` |
| pub | `#2457A6` | `#2457A6` | `#EAF0FA` | Phiên bản `Published` |
| arch | `#8E9199` | `#73767E` | (không có) | `Archived`, user đã khóa |

Ánh xạ trạng thái sang nhóm màu nằm trong **một module dùng chung** (ví dụ `src/lib/ui/status.ts`), có unit test cho mọi giá trị của `QuestionStatus`, `ExamVersionStatus`, `AiJobStatus` và trạng thái user.

### 6.2 Kiểu chữ

- **Be Vietnam Pro** (thiết kế riêng cho tiếng Việt), weight 400, 500, 600 và 700, subset `vietnamese` và `latin`, nạp bằng `next/font/google` và gắn qua biến CSS. Dùng cho toàn bộ giao diện.
- **Trang in** dùng font có chân **Noto Serif** (cùng cách nạp) cho đúng khuôn tờ đề kiểm tra. *Quyết định của Claude*: mockup đã thể hiện và người dùng không phản đối.
- Thang cỡ chữ: tiêu đề trang 26px/600, tiêu đề mục 15px/600, chữ thường 14px, chữ phụ 12.5px. Số liệu (điểm, token, chi phí) dùng `tabular-nums`.
- Không viết hoa toàn bộ nhãn. Ngoại lệ duy nhất là tiêu đề trên tờ đề in ("ĐỀ KIỂM TRA", "ĐÁP ÁN VÀ HƯỚNG DẪN CHẤM"), vì đó là khuôn văn bản thật.

### 6.3 Khung ứng dụng

```
┌───────────────┬─────────────────────────────────────────────┐
│ AI Exam Maker │ [☰]  Đề thi > (crumb)                        │ ← tấm bảng xanh ngọc
│               │      Tiêu đề trang          [Phụ] [Chính]    │
│ Trang chủ     │      Dòng mô tả ngắn                         │
│▓Ngân hàng câu▓│ (tabs nếu có)                                │
│ Sinh câu AI   ├═════════════════════════════════════════════┤ ← viền lề đỏ 4px
│ Đề thi        │ nội dung trang trên nền giấy                 │
│ …             │                                              │
│───────────────│                                              │
│ Trần Thu Hà   │                                              │
│ Giáo viên,    │                                              │
│ tổ Toán       │                                              │
│ Đăng xuất     │                                              │
└───────────────┴─────────────────────────────────────────────┘
```

**Menu dọc** (thay cho `RoleNav` và header hiện tại trong `layout.tsx`):
- Rộng khoảng 236px, nền `sidebar`, viền phải `line`. Trên cùng là tên app, tiếp theo là các mục menu, cuối cột là khối người dùng (họ tên, vai trò kèm tổ, link "Đăng xuất").
- Mục đang chọn: nền `accent`, chữ trắng, đậm 600. Mục đang mở được xác định theo đường dẫn (route con như `/questions/detail/` vẫn sáng mục "Ngân hàng câu hỏi").
- Thứ tự và phạm vi theo vai trò, giữ đúng quyền như `RoleNav` hiện tại:
  - Giáo viên: Trang chủ, Ngân hàng câu hỏi, Sinh câu bằng AI, Đề thi.
  - Tổ trưởng: như giáo viên, thêm Duyệt câu và đề.
  - Quản trị: Trang chủ, Ngân hàng câu hỏi, Đề thi, Duyệt câu và đề, Thống kê AI, Quản trị.
- Khi chưa đăng nhập, hoặc đang ở trạng thái `denied`/đang chuyển hướng: không hiện menu, chỉ hiện nội dung trạng thái (giữ hành vi `AuthGate`).

**Nút đóng/mở (☰)**:
- Là nút đầu tiên trong tấm bảng của mọi trang, có `aria-label`, `aria-expanded` và `aria-controls` trỏ tới menu.
- **Màn rộng (≥ 1024px)**: bấm để ẩn hoặc hiện cột menu, nội dung giãn ra hết chiều ngang. Trạng thái được nhớ trong `localStorage` với key `exammaker.ui.sidebar` (`open`/`closed`). Mọi thao tác đọc/ghi bọc `try/catch`; không đọc được thì mặc định mở.
- **Màn hẹp (< 1024px)**: menu mặc định đóng. Bấm ☰ thì menu trượt ra thành lớp phủ đè lên nội dung, có nền mờ phía sau. Đóng khi bấm ra ngoài, bấm Esc, hoặc chọn một mục. Khi mở thì focus chuyển vào menu, khi đóng thì trả focus về nút ☰. Trạng thái ở màn hẹp không ghi vào `localStorage`.
- Chuyển động mở/đóng tối đa 200ms. Tắt hẳn khi `prefers-reduced-motion: reduce`.

**Tấm bảng đầu trang** (component `PageHeader`): nút ☰, đường dẫn ngược (crumb, tùy trang), tiêu đề `h1`, một dòng mô tả, nhóm nút thao tác bên phải, và tab tùy chọn nằm sát mép dưới (tab đang chọn có nền `paper` liền với nội dung). Viền dưới 4px màu `margin`. Trên tấm bảng chỉ có hai kiểu nút: nút chính nền trắng chữ `accent-strong`, và nút phụ viền trắng mờ chữ trắng. Ở màn hẹp, nhóm nút xuống dòng dưới tiêu đề.

**Vùng nội dung**: nền `paper`, lề 28px (16px ở màn hẹp). Bỏ giới hạn `max-w-5xl` hiện tại. Danh sách và form giới hạn bề rộng hợp lý; màn soạn đề và bảng dùng hết chiều ngang.

### 6.4 Component dùng chung

Đặt trong `src/components/ui/`, viết bằng Tailwind và token. Trang cũ thay class tự do bằng các component này.

| Component | Mô tả |
|---|---|
| `Button` | Biến thể `primary` (nền `accent`), `secondary` (viền), `danger` (viền đỏ, chữ đỏ), `approve` (nền ok, chỉ dùng ở nút "Duyệt"), cùng `onBoard` cho hai kiểu trên tấm bảng. Cỡ `md`/`sm`. Khi `disabled`: mờ 45% và có `title` hoặc dòng giải thích ngay cạnh. |
| `PageHeader` | Như mục 6.3. |
| `ListRow` | Dòng danh sách: vạch trái 4px theo nhóm trạng thái, khối nội dung (tiêu đề, dòng meta, dòng lý do đỏ nếu có), khối cuối bên phải (chữ trạng thái và/hoặc nút). Toàn dòng là link khi có đích. Ở màn hẹp, khối cuối xuống dưới. |
| `StatusText` | Chữ trạng thái theo nhóm màu (mục 6.1), lấy nhãn từ `vi.ts`. |
| `Tag` | Nhãn nhỏ: `AI sinh`, `AI nghi ngờ đáp án` (nền rej), `AI đồng ý đáp án` (nền ok). |
| `Notice` | Khối thông báo có vạch trái: `warn`, `error`, `info`, `success`. |
| `Panel` | Khối nền trắng có viền `line`, bo 8px. Dùng cho form và cột thông tin, **không** dùng bọc từng dòng danh sách. |
| `Field`, `Input`, `Select`, `Textarea`, `Checkbox`, `Radio` | Có nhãn, gợi ý và lỗi. Focus là vòng 2px màu `accent`. |
| `FilterBar` | Hàng bộ lọc dạng select nền `tint` và checkbox "Chỉ ... của tôi". |
| `Pager` | "Trang trước", "Trang x/y", "Trang sau". |
| `Tabs` | Dùng trong tấm bảng (Duyệt, Quản trị). |
| `MetaBar` | Hàng số liệu (thời gian, số câu, tổng điểm, trạng thái) ở màn soạn và duyệt đề. |
| `EmptyState` | Tiêu đề, một câu hướng dẫn, nút hành động. |
| `DataTable` | Bảng cho Quản trị và Thống kê: header nền nhạt, số canh phải, cuộn ngang ở màn hẹp. |

Mọi phần tử tương tác có focus nhìn thấy được. Hover chỉ đổi màu hoặc viền, không có hiệu ứng nổi khối hay chuyển động trang trí.

### 6.5 Từng màn

Mockup là chuẩn tham chiếu cho **bố cục và phần nhìn** của từng màn. Khi mockup hiện dữ liệu mà văn bản này hoặc API hiện có không hỗ trợ, **văn bản này thắng**: chỉ hiện dữ liệu mà màn đã có, hoặc dữ liệu lấy được bằng các request liệt kê ở đây. Không thêm request cho từng dòng. Ghi chú bổ sung:

- **Trang chủ** `/`: tấm bảng "Xin chào, {họ tên}" và dòng vai trò kèm tổ. Một câu giới thiệu, sau đó là lưới 2 cột lối tắt theo vai trò (biểu tượng, tên, một câu mô tả; phân cách bằng đường kẻ, không dùng thẻ), rồi tới mục "Cần bạn xử lý" (mục 6.6). Lối tắt chỉ trỏ tới trang có sẵn, chính là các mục menu của vai trò đó trừ "Trang chủ". *Quyết định của Claude*: bỏ lối tắt "In đề và đáp án" trong mockup, vì in cần chọn một đề cụ thể nên không có trang đích.
- **Ngân hàng câu hỏi** `/questions/`: `FilterBar` cùng các bộ lọc hiện có, danh sách `ListRow` (tiêu đề là stem rút gọn qua `MathMarkdown`, meta "Dạng, mức độ. Chương", tag AI, lý do từ chối nếu có), `Pager`. Nút "Tạo câu hỏi" trên tấm bảng chỉ cho teacher/head.
- **Chi tiết câu hỏi** `/questions/detail/`: tấm bảng có crumb, tiêu đề, dòng "Dạng, phiên bản n", và các nút chuyển trạng thái hiện có. Câu bị từ chối hiện `Notice error` "Tổ trưởng đã từ chối: {lý do}" ở đầu. Bố cục hai cột: form bên trái; cột phải gồm `Panel` Thông tin, Kiểm chứng AI và Rà soát của tác giả. "Gửi duyệt" bị khóa khi câu AI chưa rà soát, kèm lời giải thích. Nút xóa là `danger`, đặt tách khỏi "Lưu".
- **Sinh câu bằng AI** `/ai/`: dòng quota nằm trên tấm bảng. Hai cột: form "Yêu cầu mới" (cảnh báo dưới ô ghi chú luôn hiện, dùng `Notice warn`) và "Job của tôi" là các `ListRow` (job `Running` có chấm nhấp nháy, tắt khi giảm chuyển động). *Quyết định của Claude*: dòng job **không** hiện "Đã rà soát x/y" như mockup, vì phải gọi thêm một request cho mỗi job. Tiến độ rà soát chỉ hiện ở trang kết quả job.
- **Kết quả job** `/ai/job/`: dải trạng thái (`Notice` theo trạng thái job) chứa tóm tắt và tiến độ rà soát. Mỗi câu là một khối có số thứ tự "Câu n" (thứ tự thật của job), đáp án đúng tô nền ok. Câu `Disagree` có viền và vạch đỏ. Giữ đủ các trạng thái B8 (không khả dụng, lỗi tải, bản cũ).
- **Đề thi** `/exams/`: `FilterBar` và danh sách `ListRow`. Dòng meta gồm môn, khối, người tạo, ngày cập nhật. Bên phải là "v{latestVersionNo}, {nhãn latestVersionStatus}", và nếu `publishedVersionNo` khác null thì thêm "Đang xuất bản v{publishedVersionNo}" màu pub. Màu vạch theo `latestVersionStatus`. Bấm vào dòng mở `/exams/view/?id=` như hiện nay.
- **Soạn đề** `/exams/view/`: tấm bảng gồm crumb, tiêu đề kèm link "Sửa tiêu đề", ô chọn phiên bản, dòng "Có thay đổi chưa lưu", và các nút Tạo phiên bản mới / Lưu / Gửi duyệt. Ngay dưới là `MetaBar` (thời gian sửa được, số câu, tổng điểm, "Chia đều 10 điểm", link xem đề học sinh và đáp án). Hai cột (xếp chồng khi hẹp): "Câu trong đề" có số thứ tự, ô điểm, ↑ ↓ ✕; "Ngân hàng câu hỏi" có bộ lọc và nút "Thêm". Câu chưa duyệt có viền đỏ; câu nguồn đổi revision có dòng chữ màu wait.
- **Bản in** `/exams/print/`: trên màn hình, tấm bảng gọn có crumb "Quay lại đề", sau đó một thanh công cụ trắng chứa nút chuyển "Đề học sinh / Đáp án" và "In / Lưu PDF". Tờ A4 nằm giữa nền xám, font Noto Serif, chỉ dùng màu đen. Khi in: ẩn menu, tấm bảng và thanh công cụ.
- **Hàng đợi duyệt** `/review/`: tab trên tấm bảng hiện số lượng ("Câu hỏi chờ duyệt (6)"), danh sách `ListRow` màu wait, meta ghi người gửi và thời điểm, nút "Xem và duyệt".
- **Duyệt câu** `/review/question/`: hai cột. Trái là nội dung câu. Phải là `Panel` dính khi cuộn gồm checklist, nhận xét, nút "Duyệt" (`approve`, khóa khi chưa tick đủ, có dòng giải thích) và nút "Từ chối" (`danger`).
- **Duyệt đề** `/review/exam/`: `MetaBar` có trạng thái, cảnh báo nguồn là `Notice warn`, các câu theo snapshot kèm điểm. Nút Xuất bản, Thu hồi, Lưu trữ đặt trên tấm bảng theo quyền.
- **Quản trị** `/admin/`: tab trên tấm bảng (Người dùng, Tổ chuyên môn, Môn học, Chương / Chủ đề). Nút "Đối soát Cognito" và "Tạo tài khoản" trên tấm bảng. Nội dung là `DataTable`; nút Khóa dùng kiểu `danger`. *Quyết định của Claude*: dòng mô tả trên tấm bảng chỉ hiện tổng số mục của tab đang mở, lấy từ `totalItems` của chính danh sách đó. Không gọi thêm request đếm (mockup ghi "48 tài khoản, 4 tổ chuyên môn").
- **Thống kê AI** `/admin/ai-usage/`: bộ lọc, hàng 4 số tổng (job, token vào, token ra, chi phí ước tính), một câu ghi chú "ước tính", và `DataTable`.
- **Trạng thái chung**: đang chuyển đăng nhập, đang hoàn tất đăng nhập, `denied`, cấu hình sai, mất mạng, danh sách trống. Tất cả dùng `EmptyState`, căn giữa vùng nội dung.

### 6.6 Mục "Cần bạn xử lý" trên trang chủ

Chỉ dùng API sẵn có, mỗi nhóm gọi một request. Mỗi nhóm hiện tối đa 5 dòng `ListRow`, kèm link "Xem tất cả" tới trang danh sách đầy đủ. Nhóm rỗng thì ẩn. Mọi nhóm đều rỗng thì hiện một dòng "Không có việc gì cần xử lý." Lỗi tải của một nhóm không chặn các nhóm khác (hiện một dòng lỗi nhỏ kèm "Thử lại").

| Nhóm | Ai thấy | Nguồn |
|---|---|---|
| Câu hỏi bị từ chối | teacher, head | `GET /questions?status=Rejected&mine=true` |
| Câu AI chưa rà soát | teacher, head | `GET /questions?status=Draft&mine=true` (pageSize 100), lọc phía client `source=AI && !aiReviewedByAuthor`, rồi **gom theo `aiJobId`**: mỗi job là một dòng "Còn {n} câu chưa rà soát", trỏ tới `/ai/job/?id=`. Tiêu đề dòng là môn, khối, chương lấy từ catalog đã cache; nếu chưa có thì ghi "Job sinh câu ngày {ngày}". Chỉ tính trong trang đầu; nếu `totalItems` lớn hơn 100 thì thêm chữ "trở lên" sau số. |
| Job AI đang chạy hoặc vừa lỗi | teacher, head | `GET /ai/jobs` trang đầu, lấy job `Queued`/`Running`, và job `Failed` trong 24 giờ qua |
| Câu hỏi chờ bạn duyệt | head, admin | `GET /review/questions` (dùng `totalItems` để hiện số) |
| Đề chờ bạn duyệt | head, admin | `GET /review/exam-versions` |
| Phiên bản đề bị từ chối | teacher, head | `GET /exams?mine=true&pageSize=100`, lọc phía client `latestVersionStatus == Rejected`. Mỗi đề là một dòng "{tiêu đề}, phiên bản v{n}", trỏ tới `/exams/view/?id=&vid={latestVersionId}`. Lý do từ chối không có trong danh sách nên dòng chỉ ghi "Tổ trưởng đã từ chối, mở để xem lý do". |

Dùng TanStack Query với key riêng cho trang chủ. Khi quay lại trang thì refetch; không polling.

### 6.7 Viết lại câu chữ

Nguyên tắc (theo skill `frontend-design`): gọi tên theo cách người dùng hiểu; nút nói đúng việc nó làm, và thông báo dùng cùng động từ đó ("Gửi duyệt" thì báo "Đã gửi duyệt"); lỗi nói rõ chuyện gì xảy ra và cách xử lý, không xin lỗi; trang trống thì mời hành động; viết hoa đầu câu; không nối thông tin bằng "·"; không thêm "→" sau link.

Các thay đổi đã biết (builder rà toàn bộ `vi.ts` theo nguyên tắc trên):
- Menu: "Sinh câu AI" thành "Sinh câu bằng AI"; "Duyệt câu & đề" thành "Duyệt câu và đề".
- Quota: "Hôm nay còn {x}/{y} lượt và {a}/{b} token. Làm mới lúc {giờ}."
- Tóm tắt job: "AI tạo {n} câu, bỏ {d} câu trùng với ngân hàng." và, tách riêng, "Đã rà soát {r}/{n}".
- Dòng meta danh sách: "Trắc nghiệm, Thông hiểu. Chương 2: Hàm số bậc hai".
- Trang chủ: tiêu đề "Xin chào, {họ tên}" và một câu mô tả việc app giúp làm.
- Ghi chú chi phí AI: bỏ chữ in hoa "ƯỚC TÍNH", viết thường "ước tính".
- Giữ nguyên tên trạng thái hiện có (Nháp, Chờ duyệt, Đã duyệt, Từ chối, Lưu trữ, Đã xuất bản) vì đã khớp `context.md`.
- Giữ nguyên nghĩa của câu cảnh báo dưới ô ghi chú AI (tiêu chí nghiệm thu B8). Chỉ được sửa cách diễn đạt.

### 6.8 Truy cập

- Tương phản: chữ thường đạt ít nhất 4,5:1. Cặp đã kiểm: trắng trên `board` khoảng 5,5:1; `on-board-muted` trên `board` khoảng 4,6:1; chữ wait `#8A5F05` trên trắng khoảng 5,6:1; chữ arch dùng `#73767E` vì `#8E9199` không đạt. Builder kiểm lại sau khi dựng.
- Trạng thái luôn có chữ. Màu chỉ là tín hiệu phụ.
- Mọi thao tác dùng được bằng bàn phím. Focus nhìn thấy được trên cả nền giấy và tấm bảng (trên tấm bảng dùng vòng màu trắng).
- Menu lớp phủ ở màn hẹp: giữ focus bên trong khi mở, Esc để đóng.

### 6.9 Figma

Sau khi code xong và chạy được (`npm run dev`, hoặc bản build tĩnh `out/` phục vụ cục bộ), người dùng cài plugin **html.to.design** trong Figma và nhập từng trang theo URL hoặc qua tiện ích trình duyệt (vì trang cần đăng nhập). Không phải việc của builder. Builder chỉ cần ghi hướng dẫn ngắn trong mô tả PR.

### 6.10 Tài liệu

- Phần backend (PR 1): sửa `docs/context.md` mục 9.1 hoặc phần "Chi tiết triển khai A7" để mô tả các trường mới của `Exam`, cập nhật `docs/api/openapi.yaml`, và thêm dòng lịch sử thay đổi.
- Phần frontend (PR 2): sửa `docs/context.md` **trước khi code**: mục 9.2 thêm "Chi tiết triển khai giao diện" (khung menu dọc có nút đóng/mở, tấm bảng, token, câu chữ), cập nhật tên menu ở B8 ("Sinh câu bằng AI", "Thống kê AI"), thêm mục 0.26 vào "Lịch sử thay đổi" (0.25 thuộc PR backend).
- Thêm `.superpowers/` vào `.gitignore`.

### 6.11 Backend: mở rộng `Exam` trong danh sách và chi tiết đề (PR 1)

Thêm ba trường vào schema `Exam` (dùng chung cho `GET /exams`, `GET /exams/{id}`, và response tạo/sửa đề):

| Trường | Kiểu | Ý nghĩa |
|---|---|---|
| `latestVersionId` | uuid, bắt buộc | Id của phiên bản có `versionNo` lớn nhất **trong số phiên bản người xem đọc được** |
| `latestVersionStatus` | `ExamVersionStatus`, bắt buộc | Trạng thái của chính phiên bản đó |
| `publishedVersionNo` | integer hoặc null | Số của phiên bản đang `Published` mà người xem đọc được, null nếu không có |

- **Quy tắc quyền giữ nguyên**: dùng đúng tập phiên bản đang dùng để tính `latestVersionNo` và `publishedVersionId` (`ExamRules.CanRead` trong `ExamView`, `ExamManagement.cs`). Giáo viên xem đề của người khác cùng tổ chỉ thấy phiên bản `Published`, nên các trường mới cũng chỉ phản ánh phiên bản đó, không lộ Draft hay Rejected của người khác. Ba trường mới luôn nhất quán với `latestVersionNo` và `publishedVersionId`.
- Chỉ thêm trường, không xóa hay đổi trường cũ, nên tương thích ngược. Không có migration DB.
- *Quyết định của Claude*: **không** thêm bộ lọc `latestVersionStatus` cho `GET /exams`. Nhóm "Phiên bản đề bị từ chối" trên trang chủ chỉ cần đề của chính mình (`mine=true`), số lượng nhỏ nên lọc phía client là đủ. Lọc trong SQL theo "phiên bản mới nhất người xem đọc được" khá phức tạp mà chưa có nhu cầu.
- Test backend: đề có nhiều phiên bản với các trạng thái khác nhau trả đúng ba trường theo từng vai trò (tác giả, head cùng tổ, teacher khác cùng tổ chỉ thấy bản Published, admin). Đề không có bản Published thì `publishedVersionNo` là null. Thêm test integration cho truy vấn danh sách nếu cách tính thay đổi trong SQL.
- Frontend PR 2 dùng `schema.d.ts` đã sinh lại từ PR 1.

## 7. Kiểm thử

- `npm run lint`, `npm run typecheck`, `npm test` và `npm run build` (gồm `inject-csp`) đều phải qua.
- Test hiện có: chỉ sửa khi đổi câu chữ có chủ đích hoặc vị trí của phần tử bị chuyển (ví dụ nav từ header sang menu dọc). Không xóa test hành vi.
- Test mới:
  - Ánh xạ trạng thái sang nhóm màu và nhãn, đủ mọi giá trị enum.
  - Menu dọc: đúng mục theo từng vai trò (thay `role-nav.test.tsx`), mục đang chọn theo đường dẫn.
  - Nút đóng/mở: đổi `aria-expanded`, ghi và đọc `localStorage` (kể cả khi `localStorage` ném lỗi), Esc đóng menu lớp phủ.
  - "Cần bạn xử lý": mỗi vai trò thấy đúng nhóm, nhóm rỗng thì ẩn, một nhóm lỗi không chặn nhóm khác.
- Kiểm tra bằng mắt: chạy `next dev`, chụp màn hình từng trang ở 1440px và 390px rồi so với mockup. Kiểm tra trang in bằng hộp in của trình duyệt (không có màu app, mỗi câu không bị cắt giữa trang).
- Sau khi build: mở bản `out/` và xác nhận console không có lỗi CSP, font tải từ `/_next/static`.

## 8. Để builder tự quyết

- Chia nhỏ công việc và thứ tự áp lên từng trang (gợi ý: token, font, component `ui`, khung, sau đó từng trang theo luồng demo).
- Tên file và cấu trúc thư mục bên trong `src/components/ui/`.
- Biểu tượng cho lối tắt trên trang chủ và nút ☰: ký tự Unicode hoặc SVG inline tự vẽ. Không thêm thư viện icon nếu không cần.
- Khoảng cách chi tiết và bo góc, miễn giữ tinh thần mockup: bo nhỏ (6–8px), không đổ bóng dày, dòng danh sách không biến thành thẻ.
- Cách hiện stem rút gọn trong `ListRow` (số dòng tối đa).

## 9. Điểm đã giải quyết khi rà soát

- **Trạng thái phiên bản mới nhất trong danh sách đề**: `GET /exams` không trả trạng thái phiên bản. Người dùng chọn (a), mở rộng backend (mục 6.11). Hai phương án bị loại là (b) gọi thêm `GET /exams/{id}/versions` cho từng đề, và (c) không hiện trạng thái.
- **Mockup hiện dữ liệu chưa có trong API**: văn bản thiết kế thắng (mục 6.5). Đã bỏ "Đã rà soát x/y" trên dòng job, lối tắt "In đề và đáp án", và số đếm "48 tài khoản, 4 tổ"; nhóm "Câu AI chưa rà soát" gom theo job.

## 10. Thứ tự làm

1. PR 1 (backend), nhánh `feat/exam-list-version-status` từ `dev`: tài liệu, OpenAPI, code .NET, test.
2. PR 2 (frontend), nhánh `feat/frontend-redesign` từ `dev` sau khi PR 1 vào `dev`: tài liệu `context.md`, token, font, component `ui`, khung menu, rồi từng trang, cuối cùng là trang chủ "Cần bạn xử lý".
3. Người dùng nhập các trang vào Figma (mục 6.9).
