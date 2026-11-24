# B10 — Xuất đề học sinh & đáp án: thiết kế

> Task B10 trong `docs/tasks.md`. Phụ thuộc A7 (API đề/phiên bản, snapshot) và B9 (màn soạn đề). Ngữ cảnh: `docs/context.md` mục 3.2, 4.3, 4.4, 9.

## 1. Mục đích

Bước cuối của luồng demo chính (mục 2.1): sau khi đề được duyệt/publish, giáo viên xem trước và in ra **đề học sinh** (không đáp án) và **đáp án/lời giải** (cho giáo viên) thành PDF bằng trình duyệt. Yêu cầu quan trọng nhất: **đáp án không bao giờ lọt vào đề học sinh**, có test chứng minh.

## 2. Mục tiêu / không làm / phản mục tiêu

**Mục tiêu**
- Backend: `GET /api/v1/exam-versions/{vid}/student-sheet` và `GET /api/v1/exam-versions/{vid}/answer-key`, đọc **chỉ từ snapshot**, đúng schema `StudentSheet`/`AnswerKey` trong `docs/api/openapi.yaml`.
- Frontend: trang in `/exams/print/?vid=<versionId>&mode=student|answer`, CSS in + KaTeX, nút "In / Lưu PDF" gọi `window.print()`.
- Liên kết từ trang `/exams/view/` tới hai chế độ in; thay thông báo 403 "chỉ được xem bản đề học sinh" bằng link thật.
- Test chứng minh đề học sinh không chứa đáp án ở cả backend và frontend.

**Không làm**
- DOCX, nhiều mã đề/xáo câu, lưu file xuất lên S3 (sau MVP, mục 2.3, 4.4).
- Xuất bản `Draft`/`Rejected`/`Archived`.
- Ghi audit cho thao tác xuất (thao tác đọc).

**Phản mục tiêu** (làm được nhưng vẫn là thất bại)
- Đề học sinh chứa bất kỳ trường đáp án nào: `correctLabel`, `isTrue`, `acceptedAnswers`, `tolerance`, `modelAnswer`, `rubric`, `explanation`, `difficulty`, hoặc thông tin người tạo/duyệt.
- Đọc nội dung câu hỏi gốc (bảng `questions`) thay cho snapshot.
- Trang in chế độ học sinh gọi endpoint `answer-key`.
- Render nội dung câu hỏi mà không qua sanitize (`MathMarkdown`).

## 3. Quyết định đã chốt với người dùng (24/11/2026)

1. **Quyền xuất đề học sinh của Head mở rộng bằng quyền xem đáp án** (phương án A): ai xem được đáp án thì xem được đề học sinh. Lý do: đáp án chứa toàn bộ đề học sinh, và Head cần xem bản học sinh khi duyệt. Phải sửa `context.md` mục 3.2 trước khi code.
2. **Chỉ xuất `Submitted`/`Approved`/`Published`** (cách 1). `Draft`/`Rejected` chưa có snapshot; `Archived` có thể đến từ `Rejected` với snapshot thiếu hoặc nội dung chưa từng duyệt, và DB không lưu đường đi. Áp dụng cho cả hai endpoint, mọi vai trò. Xem lại bản Archived vẫn dùng màn chi tiết B9.
3. **Backend tách đáp án bằng cách chỉ chép trường được phép (whitelist)** theo từng dạng câu, không chép nguyên payload rồi xóa trường.
4. **Frontend dùng một trang in với tham số `mode`**, mỗi chế độ chỉ gọi đúng endpoint của nó.

## 4. Phương án đã cân nhắc

**Backend**
- **Whitelist (đã chọn)**: dựng object mới cho từng dạng câu, chỉ chép trường học sinh được thấy. Trường đáp án mới thêm vào payload sau này không thể tự lọt sang. Logic thuần ở Application, test dễ.
- Blacklist (loại): chép payload rồi xóa các khóa đáp án. Code ngắn hơn, nhưng thêm trường mà quên xóa là lộ — ngược với yêu cầu chính.

**Frontend**
- **Một trang `/exams/print/?vid=&mode=` (đã chọn)**: dùng chung header/CSS in; test bảo đảm chế độ học sinh không gọi `answer-key`.
- Hai trang riêng (loại): tách bạch theo URL nhưng trùng code; lợi ích không đáng kể khi đã có test.
- In ngay trong `/exams/view` dạng hộp thoại (loại): phải ẩn cả giao diện soạn đề khi in, CSS khó.

## 5. Ràng buộc

- Static export: route dùng query string, không route động (mục 9.2). Không thêm package mới nếu không cần (đã có `react-markdown`, `remark-math`, `rehype-katex`, `rehype-sanitize`).
- Không đổi schema DB, không migration.
- Quy tắc quyền thuần nằm ở Application (`ExamRules`), dùng chung cho API.
- UI tiếng Việt trong `frontend/src/messages/vi.ts`; code/comment tiếng Anh; tài liệu `docs/` tiếng Việt.
- `context.md` phải sửa trước code (CLAUDE.md).

## 6. Thiết kế

### 6.1 Quyền & trạng thái

Quyền đọc phiên bản hiện có (`ExamRules.CanRead`, `ExamRules.cs:16`) sau quyết định 1 khớp đúng quyền xuất đề học sinh:
- Admin: tất cả.
- Tác giả (teacher/head): đề của mình (kể cả sau khi chuyển tổ).
- Head: mọi đề cùng tổ.
- Teacher: phiên bản `Published` cùng tổ.

Quyền đáp án = `CanRead` **và** `ExamRules.CanReadAnswers` (tác giả, head cùng tổ, admin).

Thứ tự kiểm, giống `VersionViewAsync` của A7:

| Bước | Điều kiện | Kết quả |
|---|---|---|
| 1 | Phiên bản không tồn tại hoặc `!CanRead(actor, exam, status)` | 404 `NotFound` (không lộ phiên bản riêng tư, nhất quán A7) |
| 2 | Chỉ answer-key: `!CanReadAnswers(actor, exam)` | 403 `Forbidden` |
| 3 | `status` không thuộc `Submitted`/`Approved`/`Published` | 409 `InvalidStateTransition` hoặc mã mới `ExportUnavailable` (xem 6.6) |
| 4 | Snapshot thiếu ở bất kỳ câu nào (phòng thủ; trigger A2 đã bảo đảm đủ ở các trạng thái này) | 409, không thay bằng nội dung câu gốc |

User mất/bị khóa: 401 như A3 (đã có sẵn qua pipeline xác thực).

Ví dụ: teacher xem đề `Approved` của người khác cùng tổ → `CanRead` false → 404. Head xem đề `Submitted` của teacher cùng tổ → 200 cả hai endpoint. Teacher xem đề `Published` của người khác cùng tổ → student-sheet 200, answer-key 403.

### 6.2 Backend: chuyển đổi snapshot (Application)

Thêm logic thuần trong `backend/src/ExamMaker.Application/Exams/` (tên file/lớp do builder đặt, ví dụ `ExamExport`):

- `StudentPayload(type, JsonElement snapshot)` trả object mới, **chỉ** gồm:

| `type` | Đầu ra |
|---|---|
| `MultipleChoice` | `{ options: [{ label, text }] }` theo thứ tự mảng |
| `TrueFalse` | `{ statements: [{ label, text }] }` |
| `ShortAnswer` | `{ answerType, unit }` (`unit` null nếu không có) |
| `Essay` | `{}` |

  Dạng không biết hoặc payload snapshot sai cấu trúc → ném lỗi, API trả 500 (log không chứa nội dung câu); **không bao giờ** trả payload thô thay thế.
- `StudentSheetItem`: `number` (= `sortOrder`), `points`, `type`, `stem` (snapshot), `payload` (ở trên). Không có `difficulty`, `explanation`, `sourceQuestionId`.
- `AnswerKeyItem`: `number`, `points`, `type`, `difficulty`, `stem`, `payload` (snapshot nguyên khối), `explanation`.
- `SheetHeader`: `examTitle`, `subjectName`, `departmentName`, `grade`, `versionNo` (= mã đề), `durationMinutes`, `totalPoints`. Dùng chung cho cả hai.

DTO là record C# riêng (`StudentSheet`, `AnswerKey`...), **không** tái dùng `ExamVersionView`/`ExamItemView`.

### 6.3 Backend: đọc dữ liệu & API

- Thêm vào `IExamManagement` (hoặc một service đọc riêng — builder chọn) hai hàm `StudentSheetAsync(actor, vid)` và `AnswerKeyAsync(actor, vid)`. Đọc `AsNoTracking`, include `Exam` → `Subject`, `Department` và `Questions` sắp theo `SortOrder`; không include `SourceQuestion` (không đọc câu gốc). Không cần khóa/transaction.
- `ExamVersionsController`: thêm `[HttpGet("{vid:guid}/student-sheet")]` và `[HttpGet("{vid:guid}/answer-key")]`, chỉ `[Authorize]` mặc định (mọi role đã đăng nhập; quyền thật ở service). Lỗi trả ProblemDetails như các endpoint A7.

### 6.4 Frontend: trang in

**Route**: `frontend/src/app/exams/print/page.tsx`, URL `/exams/print/?id=<examId>&vid=<versionId>&mode=student|answer`. Đọc `vid` và `mode` (`student` | `answer`; giá trị khác hoặc thiếu `vid` → thông báo lỗi, không gọi API). `id` chỉ dùng cho link quay lại (header không chứa examId; không đổi schema); thiếu `id` thì link quay lại về `/exams/`.

- `mode=student` chỉ gọi `GET /exam-versions/{vid}/student-sheet`; `mode=answer` chỉ gọi `/answer-key`. Hai query key khác nhau.
- Lỗi: 404 → "Không tìm thấy đề hoặc bạn không có quyền xem"; 403 (answer) → "Bạn không có quyền xem đáp án của phiên bản này"; 409 → "Phiên bản này chưa thể xuất (chỉ xuất bản đã gửi duyệt, đã duyệt hoặc đã công bố)".
- Thanh công cụ (ẩn khi in): nút "In / Lưu PDF" (`window.print()`), link chuyển sang chế độ còn lại (chỉ hiện link "Đáp án" nếu người xem có quyền — biết được khi gọi thử, hoặc luôn hiện và để trang báo 403; builder chọn), link quay lại `/exams/view/?id=&vid=`.
- Ẩn menu/role-nav của layout khi in (`@media print`). Nếu layout không cho ẩn từ trang con, dùng class trên `<body>` hoặc CSS in toàn cục ẩn các vùng điều hướng.

**Header (cả hai chế độ)**
```
<TÊN TRƯỜNG>                         ĐỀ KIỂM TRA  (đáp án: ĐÁP ÁN & HƯỚNG DẪN CHẤM)
Tổ: <departmentName>                 Môn: <subjectName> — Khối <grade>
                                     Thời gian làm bài: <durationMinutes> phút
                                     Mã đề: <versionNo>   Tổng điểm: <totalPoints>
<examTitle>
Họ và tên: ................  Lớp: ........   (chỉ đề học sinh)
```
Tên trường lấy từ `NEXT_PUBLIC_SCHOOL_NAME` (build-time, không commit giá trị thật); không cấu hình thì bỏ dòng đó. Thêm biến này (tùy chọn) vào bước build của `.github/workflows/deploy-frontend.yml` theo cách các biến `NEXT_PUBLIC_*` khác đang được truyền.

**Đề học sinh — từng câu**: "Câu <number> (<points> điểm)." + stem qua `MathMarkdown`.
- MultipleChoice: 4 phương án `A.`–`D.` (bố cục cột tùy độ dài, builder chọn).
- TrueFalse: 4 ý `a)`–`d)`, kèm cột Đúng/Sai để trống.
- ShortAnswer: dòng trả lời "........" kèm `unit` nếu có.
- Essay: khoảng trống làm bài.

**Đáp án — từng câu**: đề bài + phương án như trên, cộng:
- MultipleChoice: "Đáp án: <correctLabel>".
- TrueFalse: Đúng/Sai cho từng ý.
- ShortAnswer: các `acceptedAnswers`, `unit`, `tolerance` nếu có.
- Essay: `modelAnswer` + bảng rubric (tiêu chí, điểm).
- `explanation` nếu có ("Lời giải"). Hiện `difficulty` dạng nhãn tiếng Việt.
- Có thể thêm bảng tóm tắt đáp án trắc nghiệm ở đầu (builder chọn).

**CSS in**: khổ A4, lề hợp lý, `break-inside: avoid` cho từng câu, chữ đen trên nền trắng, KaTeX CSS đã có nạp sẵn. `points`/`tolerance` đến dạng số JSON nên trình duyệt đã parse thành float; định dạng hiển thị giống B9 (tiện ích `lib/format/decimal.ts`), không làm phép tính trên điểm ở trang in.

**Nội dung câu hỏi** (stem, text phương án/ý, modelAnswer, explanation, criterion) luôn qua `MathMarkdown`; tiêu đề đề và tên môn/tổ hiển thị text thường.

### 6.5 Frontend: liên kết từ `/exams/view/`

- Khi phiên bản đang xem có trạng thái `Submitted`/`Approved`/`Published` và người dùng đọc được chi tiết (đồng nghĩa có quyền đáp án — `VersionViewAsync` kiểm 404 rồi 403 theo `CanReadAnswers`): hiện hai nút "Xem đề học sinh" và "Xem đáp án", trỏ tới `/exams/print/?id=…&vid=…&mode=student|answer`.
- Khi đọc chi tiết trả 403 (teacher xem bản Published của người khác): thay thông báo `studentSheetOnly` bằng thông báo kèm link "Xem đề học sinh" (`mode=student`).
- Bản `Draft`/`Rejected`/`Archived`: không hiện nút xuất.

### 6.6 Tài liệu & contract (làm trước code)

- `docs/context.md`:
  - Mục 3.2, dòng "Xuất đề học sinh": Head đổi từ "như teacher" thành "đề trong tổ & đề của mình"; ghi chú chỉ xuất `Submitted`/`Approved`/`Published`.
  - Mục 4.4: thêm câu về trạng thái được xuất và lý do loại `Archived`.
  - Mục 9.2: thêm "Chi tiết triển khai B10".
  - Mục 14 "Quyết định đã chốt" + "Lịch sử thay đổi" (phiên bản 0.24).
- `docs/api/openapi.yaml`: mô tả `student-sheet` sửa quyền Head, ghi rõ `Archived` → 409, 404 khi không thấy phiên bản; mô tả `answer-key` ghi trạng thái được xuất. Sau đó sinh lại `frontend/src/lib/api/schema.d.ts` theo cách repo đang làm.
- **Mã lỗi 409**: dùng `InvalidStateTransition` sẵn có hay thêm mã `ExportUnavailable` rõ nghĩa hơn — builder chọn, ghi vào OpenAPI.

## 7. Kiểm thử

**Application/Api (unit, `ExamMaker.Api.Tests` như `ExamRulesTests`)**
- `StudentPayload` cho từng dạng: đầu ra chỉ có đúng các khóa ở 6.2; payload đầu vào cố ý chứa thêm khóa lạ (vd `secret`) cũng không lọt sang; dạng không biết ném lỗi.
- Ma trận quyền × trạng thái: admin/tác giả/head cùng tổ/head khác tổ/teacher cùng tổ/teacher khác tổ × `Draft`/`Submitted`/`Approved`/`Rejected`/`Published`/`Archived` cho cả hai endpoint, khớp bảng 6.1.

**HTTP (`ExamsHttpTests`)**
- Student-sheet trả JSON mà **toàn bộ cây** không chứa các tên khóa cấm (`correctLabel`, `isTrue`, `acceptedAnswers`, `tolerance`, `modelAnswer`, `rubric`, `explanation`, `difficulty`, `sourceQuestionId`, `createdBy`) và không chứa chuỗi giá trị đáp án đặc trưng đặt trong fixture (vd modelAnswer "MODEL-ANSWER-SENTINEL").
- Answer-key có đủ trường đáp án; mã lỗi 401/403/404/409 đúng.

**Infrastructure (PostgreSQL thật, `ExamManagementTests`)**
- Đề đi trọn luồng submit → approve → publish, sau đó **sửa câu gốc** (revision+1): student-sheet và answer-key vẫn trả nội dung snapshot cũ.
- `Archived` (từ Approved) → 409.

**Frontend (Vitest + Testing Library)**
- `mode=student`: chỉ gọi `student-sheet`, không gọi `answer-key`; render không chứa các nhãn/giá trị đáp án.
- `mode=answer`: hiện đáp án, rubric, lời giải.
- `mode` không hợp lệ/thiếu `vid`: không gọi API.
- Lỗi 403/404/409 hiện đúng thông báo.
- `/exams/view/`: nút xuất hiện đúng trạng thái; 403 hiện link đề học sinh.

Chạy `dotnet test` toàn backend (cần `A2_TEST_PG_ADMIN`), `npm` lint/typecheck/test frontend, kiểm đồng bộ schema frontend.

## 8. Để builder tự quyết

- Tên lớp/file và việc đặt hàm đọc trong `ExamManagement` hay service riêng.
- Mã lỗi 409 (6.6).
- Bố cục chi tiết trang in (cột phương án, bảng tóm tắt đáp án, khoảng trống tự luận).
- Cách hiện link "Đáp án" trên thanh công cụ trang in.
