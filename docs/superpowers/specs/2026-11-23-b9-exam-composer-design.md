# B9 — Soạn đề (frontend ghép đề)

> Thiết kế cho task B9 (`docs/tasks.md`). Ngữ cảnh: `docs/context.md` mục 3.2, 4.3, 9.2; hành vi API: `docs/a7-exam-workflow.md`, `docs/api/openapi.yaml`.

## 1. Mục đích

Giáo viên/tổ trưởng cần ghép đề từ ngân hàng câu hỏi rồi gửi tổ trưởng duyệt — bước giữa của luồng demo chính (mục 2.1): *… → ghép đề từ câu Approved → gửi tổ trưởng → …*. Hiện frontend chưa có màn nào cho việc này: `/exams/view/` chỉ là trang giữ chỗ, menu chưa có mục "Đề thi".

Backend **không cần làm thêm**: A7 đã triển khai đủ endpoint dùng chung cho B9 (`GET/POST /exams`, `GET/PUT /exams/{id}`, `GET/POST /exams/{id}/versions`, `GET/PUT /exam-versions/{vid}`, `submit`, `withdraw`, `archive`) và trả `currentSourceRevision` + `warnings` để frontend cảnh báo. B9 là công việc frontend + tài liệu.

## 2. Mục tiêu / không làm / phản mục tiêu

**Mục tiêu**
- Danh sách đề, tạo đề (tiêu đề, môn trong tổ, khối).
- Trang soạn một phiên bản: thêm/bỏ/sắp xếp câu, nhập điểm, thời lượng, lưu; gửi duyệt, rút lại, tạo phiên bản mới, lưu trữ (archive) phiên bản.
- Cảnh báo câu nguồn đổi `revision`, hiển thị `warnings` của server, đánh dấu câu chưa Approved khi gửi duyệt bị từ chối.
- Xem chỉ đọc các phiên bản từ `Submitted` trở đi (snapshot).

**Không làm**
- Màn duyệt đề, publish/unpublish (A10 đã có `/review/exam/`).
- Xuất đề học sinh/đáp án, trang in (B10). Teacher xem đề Published của người khác chỉ thấy metadata + thông báo "dùng bản đề học sinh" (link thật thêm ở B10).
- Sửa endpoint/schema backend.
- Kéo thả, tự lưu, ma trận đề, nhiều mã đề.

**Phản mục tiêu** (chạy được nhưng vẫn coi là hỏng)
- Mất thay đổi chưa lưu khi rời trang/đổi phiên bản mà không hỏi.
- Hiển thị đáp án cho người không có quyền (mọi nội dung câu chỉ lấy từ response API, không tự ghép từ nguồn khác).
- Điểm bị sai lệch do số thực (vd 0.1 + 0.2).
- Cho phép thao tác sửa trên phiên bản đã đóng băng hoặc cho admin tạo/sửa nội dung.

## 3. Ràng buộc

- Next.js static export: route động bằng query string; không middleware/SSR (context 9.2, 15).
- Stack đã có: TanStack Query, React Hook Form + Zod, `openapi-fetch` (`api` trong `lib/api/client.ts`), `MathMarkdown` (sanitize trước KaTeX), `QuestionPayloadView`, `formatDecimalForInput`, chuỗi UI trong `src/messages/vi.ts`.
- Không thêm dependency mới.
- Quyền thật ở API; UI chỉ ẩn/hiện. Tuân theo ma trận 3.2 và mục "Quyền và khóa" của A7.
- `PUT /exam-versions/{vid}` thay **toàn bộ** danh sách và giữ khóa DB (2s), có thể trả 409 khi tranh chấp.

## 4. Phương án đã cân nhắc

**Bố cục màn soạn**
- **(A) Một trang hai cột** — trái: câu trong đề; phải: ngân hàng có lọc + nút "Thêm". *Đã chọn*: thấy đề và ngân hàng cùng lúc, hợp ghép đề dài. Đánh đổi: chật trên màn hẹp → dưới breakpoint `lg` hai cột xếp chồng (ngân hàng ở dưới).
- (B) Trang đề + hộp thoại chọn câu — gọn hơn nhưng phải mở/đóng nhiều lần. Không chọn.

**Quản lý trạng thái form soạn**
- **(1) React Hook Form + `useFieldArray` + Zod** — *Đã chọn*: đúng stack (B8 dùng RHF), có `append/move/remove`, `isDirty` cho chặn rời trang, schema Zod khớp ràng buộc API. Đánh đổi: điểm giữ dạng chuỗi trong form, chuyển sang số khi gửi.
- (2) `useReducer` tự viết — logic thuần dễ test nhưng phải tự viết validate và dirty-tracking, lệch phần còn lại của frontend. Không chọn.

**Loại bỏ**: tự lưu (PUT thay toàn bộ + khóa DB → dễ 409, nhiều audit rỗng); thư viện kéo thả (thêm dependency, đã chốt nút ↑ ↓).

## 5. Thiết kế

### 5.1 Route & menu

| Route | Ai | Nội dung |
| --- | --- | --- |
| `/exams/` | mọi role đã đăng nhập | Danh sách đề (`GET /exams`), lọc môn/khối, ô "Chỉ đề của tôi" (`mine`). Teacher/head có nút "Tạo đề". |
| `/exams/view/?id=<examId>[&vid=<versionId>]` | mọi role | Trang đề: metadata, chọn phiên bản, màn soạn hoặc xem chỉ đọc. Thay trang giữ chỗ hiện có. |

Menu `RoleNav` thêm "Đề thi" (`vi.nav.exams` đã có) cho mọi role đã đăng nhập.

### 5.2 Danh sách đề `/exams/`

- Bảng: tiêu đề, môn, khối, người tạo, phiên bản mới nhất (`latestVersionNo`), đang Published hay không (`publishedVersionId`), `updatedAt` (giờ Việt Nam). Phân trang `page/pageSize` 20.
- Nút "Tạo đề" (teacher/head có `departmentId`): form RHF + Zod — tiêu đề (trim, 1–300 ký tự), môn (`fetchAllSubjects(me.departmentId)`), khối 10/11/12. `POST /exams` → điều hướng `/exams/view/?id=<id>&vid=<id bản v1>` (lấy từ `versions[0]` của `ExamDetail`). Lỗi 400 gắn vào ô tương ứng theo `errors` camelCase.
- Admin: không có nút tạo.

### 5.3 Trang đề `/exams/view/`

**Tải dữ liệu**
- `GET /exams/{id}` → `ExamDetail` (metadata + `versions` giảm dần).
- Phiên bản được chọn: `vid` trên URL nếu có và thuộc `versions`; nếu không → bản `Draft`/`Rejected` mới nhất nếu có, ngược lại bản mới nhất. Đổi phiên bản bằng dropdown, cập nhật `vid` qua `router.replace`.
- `GET /exam-versions/{vid}` → `ExamVersion`. 403 → thông báo "Bạn chỉ được xem bản đề học sinh của phiên bản này" (không gọi lại). 404 → "Không tìm thấy hoặc không có quyền".

**Phần đầu trang**
- Tiêu đề + nút "Sửa tiêu đề" (chỉ tác giả; `PUT /exams/{id}`, form nhỏ inline).
- Môn, khối (cố định), người tạo.
- Dropdown phiên bản: `v{versionNo} — {trạng thái tiếng Việt}`; bản `Rejected` hiện `reviewNote`.
- Dải `warnings` (mỗi `message` một dòng; không hiển thị mã code thô).

**Quyền chỉnh sửa trên UI**: `canEdit = me.id === exam.createdBy.id && me.role ∈ {teacher, head} && version.status ∈ {Draft, Rejected}`. Không `canEdit` → chế độ chỉ đọc.

**Chế độ soạn (canEdit)** — bố cục hai cột, xếp chồng dưới `lg`:

```
Thời gian [45] phút · Tổng 10 điểm · 8 câu
┌─ Câu trong đề ────────────┬─ Ngân hàng ───────────────┐
│ 1. ⚠ câu nguồn đã đổi      │ Chương[▾] Dạng[▾] Mức độ[▾]│
│    Cho phản ứng...   ↑ ↓ ✕ │ ☐ Chỉ câu của tôi          │
│    Điểm [1.25]             │ ─────────────────────────  │
│ 2. Nguyên tử X có... ↑ ↓ ✕ │ Câu A...   Approved [Thêm] │
│    Điểm [1.25]             │ Câu B...   Nháp ⚠   [Thêm] │
├────────────────────────────┴───────────────────────────┤
│ [Chia đều 10 điểm]  [Lưu]  [Gửi duyệt]                   │
└────────────────────────────────────────────────────────┘
```

*Form* (RHF, khởi tạo từ response, `reset` lại sau mỗi lần lưu/đổi phiên bản):
```
{ durationMinutes: number, items: { sourceQuestionId, points: string, view: ItemView }[] }
```
`view` là dữ liệu hiển thị (stem, type, difficulty, status nguồn nếu biết, `sourceRevision`, `currentSourceRevision`) — không gửi lên server. Zod: `durationMinutes` số nguyên 1–600; `items` ≤ 200, không trùng `sourceQuestionId`; `points` khớp `^\d+([.,]\d+)?$`, > 0. Gửi: `{ durationMinutes, items: items.map(i => ({ sourceQuestionId, points: Number(i.points.replace(',', '.')) })) }`.

*Cột trái — câu trong đề*
- Mỗi dòng: số thứ tự, stem qua `MathMarkdown` (rút gọn 3 dòng, bấm để mở rộng xem `QuestionPayloadView` + lời giải), nhãn dạng/mức độ, ô điểm, nút ↑ ↓ ✕ (`move`/`remove`).
- ⚠ "Câu nguồn đã thay đổi so với lúc chọn" khi `currentSourceRevision != null && currentSourceRevision !== sourceRevision`. Câu mới thêm trong phiên chưa lưu không có cảnh báo.
- Nội dung `null` (warning `SourceContentUnavailable`) → hiện "Không xem được nội dung câu này" nhưng vẫn giữ dòng, điểm, nút ✕.
- Tổng điểm và số câu tính trực tiếp từ form bằng phép cộng thập phân trên chuỗi (cộng phần nguyên và phần thập phân theo số chữ số lớn nhất — viết hàm thuần `sumDecimalStrings` trong `lib/format/decimal.ts`), không dùng float.
- "Chia đều 10 điểm": chia 10 cho n, làm tròn xuống 2 chữ số, phần dư cộng vào câu cuối để tổng đúng 10 (hàm thuần `splitPointsEvenly(total, n)`). Câu mới thêm mặc định `"1"`.

*Cột phải — ngân hàng*
- `GET /questions` (trả `QuestionPage` gồm `Question` đầy đủ: `createdBy`, `status`, `stem`, `payload`) khóa `subjectId` + `grade` của đề; lọc thêm `topicId` (`fetchAllTopics(subjectId, grade)`), `type`, `difficulty`, `mine`; phân trang 20.
- Nút "Thêm" chỉ bật khi câu **chọn được**: (`createdBy.id === me.id` và status ≠ `Archived`) hoặc status = `Approved`; và chưa có trong form. Câu không phải `Approved` hiện nhãn "Phải được duyệt trước khi gửi đề". Câu không chọn được (vd head thấy câu `Submitted` của người khác) không có nút.
- "Thêm" → `append` vào cuối form với `view` lấy từ dòng ngân hàng.

*Hành động*
- **Lưu** (bật khi `isDirty` và form hợp lệ): `PUT /exam-versions/{vid}` → `reset` theo response, cập nhật cache. 400 → lỗi theo ô (key `items[i].points`, `durationMinutes`) hoặc thông báo chung. 409 → "Đề vừa thay đổi ở nơi khác, hãy tải lại" + nút "Tải lại" (bỏ thay đổi local sau khi xác nhận).
- **Gửi duyệt** (bật khi không `isDirty` và có ≥ 1 câu; khi dirty hiện gợi ý "Lưu trước khi gửi"): xác nhận → `POST …/submit`. 409 `QuestionsNotApproved` → tô đỏ các dòng có `sourceQuestionId` nằm trong `invalidQuestionIds` (field của ProblemDetails), thông báo "Các câu sau chưa được duyệt".
- **Chặn rời trang khi dirty**: `beforeunload` + hỏi xác nhận khi đổi phiên bản/bấm link nội bộ trong trang (App Router không có API chặn điều hướng; link menu ngoài trang chấp nhận chỉ có `beforeunload` — ghi chú hạn chế).

**Hành động theo trạng thái** (chỉ tác giả; nút gọi API rồi invalidate `exam` + `examVersion`):

| Trạng thái | Nút |
| --- | --- |
| Draft | Lưu, Gửi duyệt |
| Rejected | Lưu, Gửi duyệt, Lưu trữ |
| Submitted | Rút lại (`withdraw`) |
| Approved | Lưu trữ, Tạo phiên bản mới |
| Published / Archived / Rejected | Tạo phiên bản mới |

"Tạo phiên bản mới" (`POST /exams/{id}/versions` với `fromVersionId` = bản đang xem) chỉ hiện khi đề **không** có bản Draft; thành công → chuyển `vid` sang bản mới và hiện `warnings` (vd `SourceOmitted`). 409 → "Đề đang có bản nháp" + link tới bản nháp. Lưu trữ cần hộp xác nhận ("không khôi phục được"). Head cùng tổ được archive theo 3.2 nhưng nút này ở B9 chỉ cho tác giả; head/admin dùng màn duyệt A10.

**Chế độ chỉ đọc**: cùng danh sách câu (không ô nhập/nút), thời lượng, tổng điểm, `contentHash` rút gọn nếu có, người duyệt/thời điểm, người publish.

### 5.4 Query keys & cache

`["exams", filters]`, `["exam", id]`, `["examVersion", vid]`, `["questionsPicker", filters]`. Sau mọi mutation: `setQueryData` cho version trả về, invalidate `["exam", id]` và `["exams"]`.

### 5.5 Tài liệu

- Thêm "Chi tiết triển khai B9" ở `docs/context.md` mục 9.2 + dòng lịch sử thay đổi.
- Cập nhật mục B9 trong `docs/tasks.md` khi xong.

## 6. Kiểm thử (Vitest + Testing Library, mock `api` như `ai/page.test.tsx`)

- Hàm thuần: `sumDecimalStrings`, `splitPointsEvenly`, Zod schema (điểm dấu phẩy, ≤ 0, trùng id, > 200 câu, duration biên), chọn phiên bản mặc định, `isSelectable(question, me)`.
- `/exams/`: hiển thị danh sách; admin không thấy nút tạo; tạo đề thành công điều hướng đúng URL; lỗi 400 gắn vào ô.
- `/exams/view/`:
  - tác giả + Draft: thêm câu từ ngân hàng, ↑ ↓ ✕, tổng điểm cập nhật, Lưu gửi đúng body PUT (thứ tự, điểm số);
  - cảnh báo revision chỉ khi hai revision khác nhau; nội dung null hiển thị thông báo;
  - Gửi duyệt bị tắt khi dirty; 409 `QuestionsNotApproved` tô đúng dòng;
  - 409 khi lưu hiện nút tải lại;
  - Submitted: chỉ có Rút lại; Approved/Published: chỉ đọc + Tạo phiên bản mới; có Draft thì ẩn Tạo phiên bản mới;
  - người không phải tác giả / admin: không có ô nhập hay nút sửa;
  - 403 khi đọc phiên bản → thông báo bản đề học sinh.
- Chạy `npm run lint`, typecheck, `npm test`, `next build` (static export) trước khi báo xong.

## 7. Quyết định đã chốt cùng người dùng

- Bố cục hai cột (phương án A), lưu bằng nút, không tự lưu.
- Form bằng React Hook Form + `useFieldArray` + Zod.
- Sắp xếp bằng nút ↑ ↓; nút "Chia đều 10 điểm", câu mới mặc định 1 điểm.
- Gửi duyệt / rút lại / tạo phiên bản mới nằm ở trang soạn.

## 8. Quyết định của người viết (người dùng có thể đổi)

- Chọn phiên bản mặc định: bản Draft/Rejected mới nhất, nếu không có thì bản mới nhất; `vid` trên URL.
- "Gửi duyệt" yêu cầu đã lưu (không tự lưu trước khi gửi).
- Archive ở B9 chỉ cho tác giả; head/admin dùng màn duyệt A10.
- Xếp chồng hai cột dưới breakpoint `lg`.
- Chặn rời trang: `beforeunload` + xác nhận khi đổi phiên bản trong trang; link menu chỉ có `beforeunload`.
- Cộng/chia điểm bằng chuỗi thập phân, tối đa 2 chữ số khi chia đều.

## 9. Để lại cho người làm

Tách component (vd `ExamComposer`, `QuestionPicker`, `VersionActions`), style Tailwind, câu chữ tiếng Việt cụ thể trong `vi.ts`, cách rút gọn stem.
