# B8 — Frontend AI + `/admin/ai-usage` — Thiết kế

> Task: B8 trong `docs/tasks.md`. Phụ thuộc B1 (khung frontend), B5 (API AI), B6 (worker) — đều đã có trên `dev`.
> Ngữ cảnh: `docs/context.md` mục 2.1, 4.1, 4.1.2, 6, 9.2, 10.

## 1. Mục đích

Đưa bước "Teacher sinh câu hỏi bằng AI (async) → rà soát/sửa" của luồng demo chính (context 2.1) lên giao diện, và cho admin thấy lượng dùng AI cùng chi phí ước tính để kiểm soát ngân sách $200 (context 6, 10).

## 2. Mục tiêu / không làm / phản mục tiêu

**Mục tiêu**
- Form sinh câu theo môn/khối/chương/dạng/mức độ/số câu + ghi chú, hiển thị quota hôm nay.
- **Tiêu chí nghiệm thu bắt buộc**: cảnh báo rõ ngay dưới ô ghi chú — không nhập thông tin cá nhân học sinh/giáo viên, không dán nguyên văn tài liệu có bản quyền (SGK…), vì prompt được gửi qua Bedrock cross-region.
- Theo dõi job bằng polling có backoff, hiển thị rõ `Queued` / `Running` / `Succeeded` / `Failed`.
- Rời trang rồi quay lại vẫn xem được job (dữ liệu từ server, không phụ thuộc state trang).
- Rà soát từng câu vừa sinh ngay trên trang job, có nút **"Đã rà soát"**.
- Nội dung AI hiển thị qua Markdown + KaTeX đã sanitize.
- Trang `/admin/ai-usage` **và** endpoint backend `GET /api/v1/admin/ai-usage` (chưa có — làm trong PR này).

**Không làm**
- Không viết lại form sửa câu hỏi: nút "Sửa" mở trang chi tiết câu hỏi hiện có (`/questions/detail/?id=`).
- Không đổi schema DB, không đổi worker B6, không đổi schema request/response của 4 endpoint `/ai/*` (chỉ thêm một kiểm tra 400 ở `POST /ai/generate`, mục 5.7).
- Không làm biểu đồ thống kê; chỉ bảng + tổng.

**Phản mục tiêu** (làm vậy là hỏng dù "chạy được")
- Render HTML thô từ nội dung AI, hoặc KaTeX chạy trước sanitize.
- Polling không dừng khi job đã kết thúc, hoặc polling dồn dập (gây tải/tốn request) khi job chờ lâu (EC2 tắt ban đêm).
- Chi phí hiển thị như hóa đơn thật — phải ghi rõ "ước tính".
- Cảnh báo ghi chú bị ẩn/thu gọn hoặc chỉ hiện khi focus.

## 3. Ràng buộc

- Frontend **static export**: route động bằng query string (`/ai/job/?id=`), `trailingSlash: true`, không dùng tính năng cần Node server (context 9.2, 15).
- Chuỗi giao diện tiếng Việt trong `frontend/src/messages/vi.ts`; code tiếng Anh.
- Gọi API qua `openapi-fetch` (`@/lib/api/client`) với type sinh từ `docs/api/openapi.yaml`; dropdown danh mục dùng `@/lib/api/catalog`.
- Quyền thật ở backend. UI: teacher/head thấy "Sinh câu AI"; admin thấy "Thống kê AI" (admin **không** sinh AI — `POST /ai/generate` trả 403).
- Backend `/admin/ai-usage`: Admin policy, phân trang 1/20 tối đa 100, lỗi `application/problem+json`, ngày UTC.
- Đổi quy tắc/kiến trúc → cập nhật `docs/context.md` trước (thêm "Chi tiết triển khai B8", mục lịch sử 0.22).

## 4. Các phương án đã cân nhắc

### 4.1 Tính `estimatedCostUsd` — **chọn A (kết hợp)**

| | Cách | Đánh đổi |
|---|---|---|
| **A ✔** | Dòng `jobs`/`tokens` lấy từ `ai_usage_daily` như contract; chi phí tính từ `ai_generation_jobs` (`input_tokens`, `output_tokens`, `model_id`) gom theo (người yêu cầu, ngày chi phí của job — mục 5.5), nhân bảng giá theo model trong cấu hình | Đúng giá theo model (Haiku dev / Sonnet demo), không migration. Job có lần gọi model rải qua nhiều ngày UTC (retry qua đêm) vẫn có thể lệch chi phí một ngày — chấp nhận vì là ước tính |
| B | Migration tách `ai_usage_daily` thành input/output | Đụng schema + worker B6; vẫn không biết model → một giá chung |
| C | Một giá trung bình/1M token | Sai lớn (output ×5 input, Sonnet ×3 Haiku) |

Người dùng chọn **A**.

### 4.2 Rà soát câu vừa sinh — **chọn 1 (ngay trên trang job)**

| | Cách | Đánh đổi |
|---|---|---|
| **1 ✔** | Trang job hiển thị thẻ từng câu (đề, phương án/đáp án, lời giải, cờ kiểm chứng AI), mỗi thẻ có **Đã rà soát** (gọi API trực tiếp) và **Sửa** (mở trang chi tiết) | Xem cả lô một màn, bấm nhanh; không nhân đôi form sửa |
| 2 | Trang job chỉ là danh sách link sang trang chi tiết | Ít code hơn nhưng đi lại 10 lần mỗi lô |

Người dùng chọn **1**.

## 5. Thiết kế

### 5.1 Trang & điều hướng

| Route | Ai | Nội dung |
|---|---|---|
| `/ai/` | teacher, head | Form sinh câu (trên) + "Job của tôi" (dưới, `GET /ai/jobs`, phân trang, mới nhất trước) |
| `/ai/job/?id=<jobId>` | teacher, head (admin xem được nếu mở link, chỉ đọc) | Trạng thái job + polling + thẻ rà soát khi `Succeeded` |
| `/admin/ai-usage/` | admin | Bảng thống kê dùng AI |

`RoleNav` thêm link "Sinh câu AI" (teacher/head) và "Thống kê AI" (admin). Trang admin hiện tại (`/admin/`) thêm link sang `/admin/ai-usage/`.

### 5.2 Form sinh câu (`/ai/`)

```
Quota hôm nay: 3/10 lượt · 24.500/100.000 token · làm mới lúc 07:00 (giờ VN)
Môn [▼]  Khối [10|11|12]  Chương [▼]
Dạng câu [▼]  Mức độ [▼]  Số câu [  5 ] (tối đa 10)
Ghi chú yêu cầu thêm
┌──────────────────────────────────────────┐
│                                          │
└──────────────────────────────────────────┘
⚠ Không nhập thông tin cá nhân của học sinh/giáo viên và không dán
  nguyên văn tài liệu có bản quyền (SGK, đề thương mại). Nội dung này
  được gửi tới dịch vụ AI và có thể được xử lý ngoài Việt Nam/Singapore.
                                   0/1000 ký tự      [Sinh câu hỏi]
```

- React Hook Form + Zod, theo khuôn các form hiện có. Môn → khối → chương lọc dây chuyền (`fetchAllSubjects`, `GET /topics?subjectId=&grade=`); đổi môn/khối thì xóa chương đã chọn.
- `count`: số nguyên 1..`quota.maxQuestionsPerJob`. `note`: tối đa 1000 ký tự, trim, rỗng → `null`.
- Cảnh báo ghi chú **luôn hiển thị** dưới ô ghi chú (không phụ thuộc focus), có `role="note"` hoặc gắn `aria-describedby` vào textarea.
- Quota: `GET /ai/quota`; `resetsAt` hiển thị theo giờ Việt Nam. Khi `jobsUsed >= jobsLimit` hoặc `tokensUsed >= tokensLimit` → vô hiệu nút gửi và hiện lý do.
- Gửi thành công (202) → invalidate quota + danh sách job → điều hướng `/ai/job/?id=<id>`.
- Lỗi: 400 → gắn lỗi vào field theo key camelCase (`errors[field]`); 429 `QuotaExceeded` → "Đã hết quota AI hôm nay"; 429 `RateLimited` → "Thao tác quá nhanh, thử lại sau N giây" (đọc `Retry-After` nếu có); 403 → thông báo không có quyền.
- **Danh sách "Job của tôi"**: mỗi dòng = thời gian tạo, môn/chương/dạng/số câu (từ `params`; tên môn/chương tra từ danh mục đã tải, không có thì hiện "—"), badge trạng thái, link "Xem". Danh sách tự refetch khi có job chưa kết thúc (khoảng 15s), dừng khi tất cả đã kết thúc.

### 5.3 Trang job (`/ai/job/?id=`)

**Polling**: TanStack Query `refetchInterval` là hàm của số lần đã poll: 2s, 4s, 8s, rồi giữ 15s; trả `false` khi `status ∈ {Succeeded, Failed}`. Không poll khi tab ẩn (mặc định TanStack). Logic backoff tách thành hàm thuần `nextPollDelay(attemptIndex, status)` để test. Lỗi mạng/5xx khi poll: giữ dữ liệu cũ, hiện dòng "Mất kết nối, đang thử lại…", tiếp tục theo backoff. 404 → "Không tìm thấy job".

**Hiển thị theo trạng thái**

| Trạng thái | Hiển thị |
|---|---|
| `Queued`, `attempt = 0` | "Đang chờ xử lý…" + spinner |
| `Queued`, `attempt > 0` | "Đang chờ thử lại (lần {attempt}/{maxAttempts})" + `errorMessage` nếu có |
| `Queued` quá 15 phút từ `createdAt` | thêm gợi ý "Hệ thống xử lý AI có thể đang tạm dừng; job sẽ chạy khi hệ thống hoạt động lại." |
| `Running` | "AI đang sinh câu hỏi (lần {attempt}/{maxAttempts})…" |
| `Failed` | Hộp lỗi: `errorMessage` (câu tiếng Việt cố định từ server; không có thì câu chung) + nút **"Sinh lại với cùng yêu cầu"** → `/ai/?from=<jobId>` điền sẵn form từ `params` của job đó |
| `Succeeded` | Tóm tắt "{n} câu · {duplicatesDropped} câu trùng đã bỏ · Đã rà soát {k}/{n}" + danh sách thẻ |

`Succeeded` với 0 câu (tất cả trùng): thông báo "Mọi câu AI sinh ra đều trùng với ngân hàng, không có câu mới." + nút sinh lại.

Header job luôn hiện tham số (môn/khối/chương/dạng/mức độ/số câu, ghi chú nếu có — ghi chú render dạng **text thường**, không Markdown).

**Thẻ câu hỏi** (khi `Succeeded`): tải từng câu bằng `GET /questions/{id}` cho mỗi `questionIds` (≤ 10 request song song qua `useQueries`), giữ thứ tự `questionIds`.

```
Câu 1   [⚠ AI không đồng ý đáp án — cần xem kỹ]   Trạng thái: Nháp
Cho hàm số $y=x^3-3x$ …
  A. …   B. …   ✓ C. …   D. …
Lời giải: …
Ghi chú kiểm chứng: …
                                  [Sửa]   [✓ Đã rà soát]
```

- Nội dung câu dùng `MathMarkdown` (stem, text phương án/ý, lời giải, đáp mẫu Essay) và tái sử dụng `QuestionPayloadView` hiện có nếu phù hợp; đáp án đúng được đánh dấu (trang chỉ dành cho tác giả).
- Cờ: `Disagree` → nhãn đỏ "cần xem kỹ" + `aiVerificationNote`; `Agree` → nhãn xanh; `NotChecked` → nhãn xám.
- **Đã rà soát**: chỉ hiện khi người xem là tác giả, `source = AI`, `aiReviewedByAuthor = false` và trạng thái cho phép (theo cùng điều kiện trang chi tiết đang dùng). Gọi `POST /questions/{id}/mark-reviewed`, thành công → cập nhật thẻ (đã rà soát ✓) và bộ đếm. Lỗi → hiện thông báo trên thẻ.
- **Sửa** → `/questions/detail/?id=<id>`.
- Câu không đọc được (404/403 — đã xóa hoặc không còn quyền): thẻ "Câu này không còn khả dụng".
- Câu đã không còn `Draft` (đã gửi duyệt, …): hiện trạng thái, ẩn nút rà soát nếu đã rà soát.

### 5.4 Sanitize nội dung AI

Dùng `MathMarkdown` hiện có (`rehype-sanitize` trước `rehype-katex`, không render HTML thô). Không thêm `dangerouslySetInnerHTML`. Test: stem chứa `<script>`/`<img onerror>` không tạo phần tử thực thi; công thức `$...$` vẫn render KaTeX.

### 5.5 Backend `GET /api/v1/admin/ai-usage`

Theo đúng contract đã có trong `docs/api/openapi.yaml` (`AiUsagePage`, `AiUsageRow`).

- **Controller** `AdminAiUsageController` (`api/v1/admin/ai-usage`, `[Authorize(Policy = AccessPolicies.Admin)]`), query: `page`, `pageSize`, `userId?`, `from?`, `to?` (date `YYYY-MM-DD`, cận gồm hai đầu).
- **Application**: `AiUsageReportQueries` validate (page ≥ 1, 1 ≤ pageSize ≤ 100, `from <= to`, định dạng ngày) → 400 `ValidationFailed` với key `page`/`pageSize`/`from`/`to`; gọi `IAiUsageReportReader`.
- **Infrastructure** `EfAiUsageReportReader`:
  - Dòng: `ai_usage_daily` lọc theo user/ngày, join `users` để lấy `UserRef`, sắp `date` giảm dần rồi `user_id` (ổn định phân trang).
  - **Ngày chi phí của job** = ngày UTC của `COALESCE(finished_at, started_at, created_at)`. *(Quyết định của Claude: EC2 tắt 16:00–24:00 UTC nên job xếp hàng trước giờ tắt thường chạy sau 0h UTC; token được cộng vào `ai_usage_daily` theo ngày gọi model, nên ngày chạy khớp cột token hơn ngày tạo.)*
  - Chi phí dòng: tổng trên `ai_generation_jobs` có `requested_by = user_id` và ngày chi phí = `date`, gom theo `model_id`: `input_tokens/1e6 × InputPer1MUsd + output_tokens/1e6 × OutputPer1MUsd`. Chỉ tính cho các (user, ngày) của trang hiện tại.
  - `totals.jobs`, `totals.tokens`: tổng `ai_usage_daily` trên **toàn bộ** kết quả lọc. `totals.estimatedCostUsd`: tổng chi phí job trên toàn bộ bộ lọc (cùng quy tắc user/ngày chi phí).
  - Job có `model_id` NULL (chưa chạy) có token 0 → không ảnh hưởng. `model_id` không có trong bảng giá → chi phí 0 cho phần đó + log `Warning` một lần mỗi request kèm model id (model id không phải dữ liệu nhạy cảm).
  - Tính bằng `decimal`, làm tròn 4 chữ số khi trả về.
- **Cấu hình bảng giá** (`AiPricingOptions`, section `AiPricing`) — **dạng danh sách** vì model id chứa `:` (ký tự phân cấp key cấu hình .NET):
  ```
  AiPricing:Models:0:ModelId          = global.anthropic.claude-haiku-4-5-20251001-v1:0
  AiPricing:Models:0:InputPer1MUsd    = 1.00
  AiPricing:Models:0:OutputPer1MUsd   = 5.00
  AiPricing:Models:1:ModelId          = global.anthropic.claude-sonnet-4-6
  AiPricing:Models:1:InputPer1MUsd    = 3.00
  AiPricing:Models:1:OutputPer1MUsd   = 15.00
  ```
  Giá trị mặc định trong `appsettings.json` theo đơn giá context mục 6; ghi đè qua Parameter Store `/exammaker/dev/AiPricing/Models/{i}/...`. Giá âm/trùng `ModelId` → khởi động thất bại.
- **Test**: integration (PostgreSQL thật) — teacher/head → 403; lọc user/ngày; phân trang + totals toàn bộ; chi phí đúng theo 2 model; model lạ → 0; job khác ngày không cộng nhầm; 400 cho tham số sai.

### 5.6 Trang `/admin/ai-usage/`

```
Thống kê sử dụng AI   (chi phí là ƯỚC TÍNH theo bảng giá cấu hình, không phải hóa đơn AWS)
Người dùng [Tất cả ▼]   Từ [YYYY-MM-DD]  Đến [YYYY-MM-DD]   [Lọc]
┌────────────┬──────────────────┬──────┬─────────┬────────────┐
│ Ngày (UTC) │ Người dùng       │ Job  │ Token   │ Chi phí ≈  │
├────────────┼──────────────────┼──────┼─────────┼────────────┤
│ 2026-11-23 │ Nguyễn Văn A     │ 4    │ 31.200  │ $0,0910    │
└────────────┴──────────────────┴──────┴─────────┴────────────┘
Tổng (toàn bộ kết quả lọc): 12 job · 96.400 token · ≈ $0,2810      « 1/3 »
```

- Dropdown người dùng lấy từ `GET /admin/users` (mọi trang). Ngày là ngày UTC (ghi rõ trên cột). Số định dạng `vi-VN`.
- Không phải admin → không render nội dung (API vẫn chặn 403).

### 5.7 Chặn sinh AI cho môn ngoài tổ (`POST /ai/generate`)

**Vấn đề**: `AiGenerationService.ValidateCatalogAsync` chỉ kiểm môn/chương tồn tại, không kiểm môn thuộc tổ người gửi. Job vẫn được tạo và trừ 1 lượt quota, rồi worker B6 đánh `Failed` `RequesterUnavailable`; lượt không được hoàn.

**Người dùng chọn**: chặn ở cả hai phía.

- **Backend**: trong bước validate catalog (trước khi giữ quota/tạo job), môn phải thuộc `department_id` hiện tại của người gửi (đọc từ DB qua user đã xác thực). Sai → 400 `ValidationFailed` `errors[subjectId]` với câu tiếng Việt cố định (vd "Môn học không thuộc tổ chuyên môn của bạn."). Không tạo job, không trừ quota, không enqueue. Kiểm lại trong transaction tạo job (cùng chỗ đã kiểm lại catalog dưới shared lock 48683402) để không lọt khi môn bị chuyển tổ giữa chừng — nếu kiểm lại thất bại thì xử lý như nhánh "danh mục đổi sau validation" hiện có (400, không tiêu quota).
- **Frontend**: dropdown môn chỉ lấy môn của tổ mình (`fetchAllSubjects(me.departmentId)`); 400 `subjectId` gắn vào field môn.
- **Test**: unit/integration — môn tổ khác → 400, quota không đổi, không có job, không gửi SQS; môn cùng tổ vẫn 202.
- **Tài liệu**: ghi quy tắc vào `docs/context.md` (Chi tiết triển khai B8, và nhắc ở 4.1) + mô tả 400 ở `/ai/generate` trong `docs/api/openapi.yaml`.

## 6. Tài liệu & contract

- `docs/context.md`: thêm "Chi tiết triển khai B8" (polling/backoff, cảnh báo ghi chú, cách tính chi phí + cấu hình `AiPricing`), cập nhật lịch sử 0.22; cập nhật mục 9.1 nếu cần.
- `docs/api/openapi.yaml`: không đổi schema; bổ sung mô tả cách ước tính chi phí ở `/admin/ai-usage` nếu cần, rồi chạy `npm run api:generate` + `api:check`.
- `docs/tasks.md`: tick B8 khi PR merge.

## 7. Kiểm thử frontend (Vitest + Testing Library)

- `nextPollDelay`: dãy 2/4/8/15/15…s, `false` khi kết thúc.
- Form: cảnh báo ghi chú luôn hiển thị; Zod chặn `count` > max và note > 1000; 429 hiển thị đúng thông điệp.
- Trang job: mỗi trạng thái hiển thị đúng; `Succeeded` render thẻ, bấm "Đã rà soát" gọi đúng endpoint và cập nhật bộ đếm; `Failed` có nút sinh lại dẫn tới `/ai/?from=`.
- Sanitize: nội dung `<script>`/`<img onerror>` không render phần tử thực thi.
- `/admin/ai-usage`: render bảng + tổng, gửi đúng query lọc.

## 8. Để người làm tự quyết

- Bố cục/màu Tailwind chi tiết, theo phong cách các trang hiện có.
- Vị trí chính xác của kiểm tra tổ trong B5 (service hay store), miễn đảm bảo các tính chất ở 5.7.
- Tách component/file trong `src/app/ai/` và `src/components/ai/`.
- Có tái sử dụng `QuestionPayloadView` nguyên trạng hay bọc thêm.
