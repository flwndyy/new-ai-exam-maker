# A6 — Ngân hàng câu hỏi và quy trình duyệt

Triển khai API câu hỏi theo OpenAPI, dùng schema A2, auth A3 và danh mục A5. Không thêm migration, dịch vụ AWS hoặc thay đổi snapshot của đề thi. Nội dung Markdown/LaTeX là dữ liệu không tin cậy; frontend vẫn phải sanitize khi hiển thị.

## Endpoint

Tất cả đường dẫn có tiền tố `/api/v1` và yêu cầu tài khoản nội bộ active.

| Endpoint | Tác dụng | Quyền |
|---|---|---|
| `GET /questions` | Ngân hàng, phân trang/lọc | Tác giả xem câu của mình; Approved cùng tổ; admin xem tất cả |
| `GET /questions/{id}` | Chi tiết, trả 404 nếu ngoài phạm vi | Như ngân hàng; head còn đọc Submitted cùng tổ để duyệt |
| `POST /questions` | Tạo Manual, Draft, revision 1; trả 201 + Location | Teacher/head, môn thuộc tổ hiện tại |
| `PUT /questions/{id}` | Sửa nội dung | Creator teacher/head; Draft/Rejected/Approved |
| `DELETE /questions/{id}` | Xóa câu chưa có liên kết đề; liên kết bộ được gỡ cùng transaction | Creator teacher/head; Draft/Rejected |
| `POST /questions/{id}/submit` | Gửi duyệt | Creator; Draft/Rejected |
| `POST /questions/{id}/withdraw` | Rút lại | Creator; Submitted |
| `POST /questions/{id}/approve` | Duyệt; body bắt buộc `{submittedAt, note?}` của lượt đã đọc | Head cùng tổ hoặc admin, cấm tự duyệt |
| `POST /questions/{id}/reject` | Từ chối; body `{submittedAt, note}` bắt buộc | Cùng quyền approve |
| `POST /questions/{id}/archive` | Approved → Archived, không khôi phục; chặn khi đề Draft/Rejected/Submitted tham chiếu | Teacher sở hữu; head sở hữu hoặc cùng tổ; admin |
| `POST /questions/{id}/mark-reviewed` | Đặt aiReviewedByAuthor=true | Creator teacher/head; AI Draft/Rejected |
| `GET /review/questions` | Submitted theo submittedAt tăng dần, loại câu của mình | Head trong tổ; admin tất cả |

Danh sách ngân hàng sắp createdAt giảm dần, thêm id để thứ tự ổn định. Hàng đợi thêm id sau submittedAt. Mặc định page=1/pageSize=20; tối đa 100; tổng trang bằng 0 khi không có kết quả, trang vượt dữ liệu có items rỗng. Ngân hàng hỗ trợ status, subjectId, topicId, grade, type, difficulty, mine, aiJobId; hàng đợi chỉ hỗ trợ subjectId/topicId và phân trang. Count và lấy trang là hai truy vấn, có thể phản ánh hai thời điểm gần nhau khi có ghi đồng thời.

## Các hàm có tác dụng gì?

Code dùng comment kỹ thuật tiếng Anh theo quy ước dự án. Giải thích tiếng Việt:

Validation nghiệp vụ dùng các hàm thuần ở Application (QuestionRules/QuestionPayloadRules), không dùng FluentValidation. MVC giữ kiểm binding/DataAnnotations và dùng tên JSON cho lỗi field.

| Hàm / thành phần | Tác dụng |
|---|---|
| `QuestionRules.CanRead` | Kiểm quyền đọc theo role, trạng thái, tổ và người tạo; dùng cho GET chi tiết. |
| `QuestionRules.RequireOwner` | Chặn thao tác lên nội dung của người khác và chặn admin tạo/sửa nội dung. |
| `QuestionRules.RequireArchive` | Kiểm quyền và trạng thái archive trước khi tra tham chiếu đề; head vẫn có quyền với câu của mình sau chuyển tổ. |
| `QuestionRules.Validate` | Kiểm input câu hỏi và gọi kiểm payload theo dạng trước khi lưu/gửi duyệt. |
| `QuestionPayloadRules.Validate` | Kiểm schema đúng type; không đoán theo hình dạng JSON. |
| `Object/Labels/Text/Array` | Chặn field lạ/trùng/thiếu, nhãn trùng, boolean sai kiểu, câu trả lời rỗng và rubric điểm không hợp lệ. |
| `QuestionRules.Edit` | So nội dung thật sự; Approved đổi nội dung → Draft, revision+1; reset kiểm chứng/rà soát AI khi sửa. |
| `QuestionRules.HasContentChanges` | So sánh input đã chuẩn hóa với câu hiện tại để kiểm liên kết đề trước khi sửa; không biến lần lưu không đổi nội dung thành lỗi. |
| `QuestionRules.Transition` | State machine thuần: kiểm quyền trước, sau đó chuyển trạng thái, thời điểm và quyết định duyệt. Không gọi DB/AWS. |
| `QuestionManagement.ActorAsync` | Đọc lại trạng thái/role/tổ từ DB; không tin dữ liệu quyền trong body/token cache. |
| `CatalogAsync` | Kiểm bộ topic/subject/grade và lấy department từ catalog, giữ department câu cố định khi sửa. |
| `MutateAsync` | Mở transaction, lấy khóa, kiểm actor, thực hiện thay đổi, lưu audit và commit cùng nhau. |
| `LockQuestionAsync` | Giữ FOR UPDATE và reload trước khi quyết định để hai reviewer không ghi đè kết quả. |
| `EnsureSourceChangeAllowedAsync` | Khóa các phiên bản đề tham chiếu; chặn mở lại/archive nguồn và đổi phân loại khi còn đề Draft/Rejected/Submitted. |
| `CreateAsync/UpdateAsync` | Tạo hoặc sửa câu theo quyền và ràng buộc danh mục; không cho client giả mạo source/creator/revision/status. |
| `TransitionAsync` | Đọc câu có khóa, gọi state machine ở Application và ghi audit từ trạng thái cũ sang mới. |
| `DeleteAsync` | Kiểm câu Draft/Rejected thuộc tác giả, kiểm liên kết đề, tự gỡ liên kết bộ, xóa bằng PostgreSQL và lưu lịch sử. |
| `ListAsync/ReviewQueueAsync/GetAsync` | Áp phạm vi nhìn thấy trong DB trước phân trang; GET ngoài phạm vi trả 404. |
| `View` | Đổi entity thành DTO đúng OpenAPI, payload là object JSON, createdBy/reviewedBy là UserRef. |
| `QuestionExceptionHandler.TryHandleAsync` | Trả ProblemDetails code/traceId; validation thêm errors[field], không trả SQL/stack cho client. |
| `QuestionsController/QuestionReviewController` | Bind HTTP và lấy actor từ ICurrentUser, áp policy thô; service vẫn kiểm quyền DB cụ thể. |

## Trạng thái và lỗi

| Hành động | Trạng thái đầu | Trạng thái sau |
|---|---|---|
| Submit | Draft / Rejected | Submitted |
| Withdraw | Submitted | Draft |
| Approve | Submitted | Approved |
| Reject | Submitted | Rejected |
| Archive | Approved | Archived |
| Mark-reviewed | AI Draft / Rejected | Giữ trạng thái, đặt cờ rà soát |
| Sửa nội dung | Approved | Draft, revision+1 |
| Sửa nội dung | Draft / Rejected | Giữ trạng thái và revision |

- Không có đường khôi phục Archived. Submitted phải withdraw trước khi sửa.
- PUT cùng nội dung (so payload theo cấu trúc JSON) không tăng revision hoặc mở lại Approved.
- Sửa nội dung thực sự của câu Approved hoặc archive bị chặn bằng 409 `QuestionReferenced` khi đề Draft/Rejected/Submitted còn tham chiếu, kể cả đề của người khác. Submitted có thể withdraw/reject nên cần bảo vệ câu nguồn. Phải gỡ câu khỏi phiên bản có thể sửa trước khi mở lại/archive câu; PUT không đổi nội dung vẫn thành công. Cùng guard bảo vệ đổi môn/chương/khối. Chỉ còn đề Approved/Published/Archived thì cho sửa/archive nguồn, không thay snapshot.
- Giữ chính sách chặn trên, không cho tác giả tự sửa đề của người khác. Lỗi có `references.totalItems` và tối đa 20 `items` gồm examId/versionId/status/createdBy (id/fullName) để liên hệ chủ đề gỡ câu; Submitted cần withdraw/reject trước. Không trả tiêu đề/nội dung đề hay email. Khi còn nhiều hơn 20 liên kết, xử lý các liên kết hiển thị rồi thử lại để thấy phần còn lại. Đây không phải quyền đọc/sửa đề riêng tư.
- Approve/reject phải gửi đúng submittedAt từ lần GET reviewer đã đọc. So sánh dưới khóa hàng sau kiểm quyền/trạng thái: thiếu trả 400 errors[submittedAt], lệch lượt trả 409 Conflict và không audit/thay trạng thái. Client phải tải lại, đọc nội dung mới và ra quyết định lại; không tự thay token để retry. Contract A7 dùng cùng tiền điều kiện này khi triển khai endpoint phiên bản đề.
- AI chưa rà soát không submit được: 409 `AiReviewRequired`. Sửa nội dung AI reset aiReviewedByAuthor, aiVerification và aiVerificationNote, giữ nguyên job/ordinal/source.
- Submit xóa quyết định cũ và đặt submittedAt mới. Withdraw xóa submittedAt và quyết định duyệt. Reject note không trắng, tối đa 2000; approve note tùy chọn, tối đa 2000. Reviewer và thời gian lưu UTC.
- Actor không tồn tại hoặc bị khóa lúc kiểm lại DB trả 401 `Unauthorized` kèm `WWW-Authenticate: Bearer`, kể cả sau khi chờ khóa. Actor hợp lệ nhưng thiếu quyền trả 403 `Forbidden`; tự duyệt của head/admin trả 403 `SelfReview`. Sai trạng thái trả 409 `InvalidStateTransition`.
- Câu bị exam_version_questions tham chiếu trả 409 `QuestionReferenced`. Liên kết question_set_items được tự xóa trong cùng transaction trước khi xóa câu đủ điều kiện, kể cả thuộc bộ của người khác; không xóa bộ và không sửa snapshot.
- Mỗi bộ thực sự mất liên kết có một audit `QuestionSet` / `RemoveQuestionFromSet`, ghi actor và id/revision câu bị xóa. Dùng `DELETE ... RETURNING` để ghi đúng các bộ bị ảnh hưởng; các audit này commit hoặc rollback cùng `DeleteQuestion` và dữ liệu.
- Approve/reject câu hỏi dùng schema riêng `QuestionApproveRequest` / `QuestionRejectRequest`, từ chối field ngoài hợp đồng. Schema `ApproveRequest` / `RejectRequest` của exam-version cũng bắt buộc submittedAt để A7 triển khai cùng tiền điều kiện; không áp đặt từ chối field thừa lên A7 trong lượt này.
- Validation trả 400 `ValidationFailed` với errors[field]; body không cho field ngoài hợp đồng. Bốn dạng payload đều kiểm: MC đúng 4 nhãn A–D/1 correctLabel; TrueFalse 4 nhãn a–d và boolean; ShortAnswer có đáp án, tolerance chỉ cho number; Essay có rubric điểm dương. Tổng rubric bằng điểm câu kiểm khi ghép đề ở A7.
- Lỗi binding/DataAnnotations cũng dùng key camelCase như `stem`, `type`, `subjectId`, `grade`, bỏ tiền tố `$.` của JSON path và giữ code/traceId. Create kiểm actor live và role trước validation nghiệp vụ; lỗi bind/Required của MVC vẫn trả trước action.
- Điểm rubric kiểm dấu và phần định trị trên số JSON gốc, không ép qua decimal; `1e-30` và `1e-1000` hợp lệ, số âm và mọi dạng số 0 đều không hợp lệ. Mỗi token số tối đa 1024 ký tự và số mũ tường minh trong [-1000, 1000]; ngoài giới hạn trả 400 errors[payload] trước PostgreSQL. Explanation trim/blank thành null trước kiểm độ dài 10000, vẫn cấm NUL.
- Options/statements phải theo thứ tự A–D/a–d. ShortAnswer number chỉ nhận `-?[0-9]+([.,][0-9]+)?`, giữ nguyên chuỗi đáp án; không nhận khoảng trắng, dấu cộng, số mũ, NaN/Infinity. Text payload tối đa 10000 ký tự, acceptedAnswers/rubric tối đa 20 phần tử; surrogate lẻ trả 400 `errors[payload]`.
- Mark-reviewed đã hoàn tất là no-op sau kiểm quyền/trạng thái: không thêm audit hoặc đổi updatedAt. Câu mới dùng cùng mốc UTC cho createdAt/updatedAt.

## Transaction và đồng thời

Mỗi ghi giữ `FOR SHARE` trên hàng user của actor và đọc quyền live, sau đó giữ shared catalog lock `48683402` rồi khóa hàng câu hỏi. Khóa hàng actor ngăn thay đổi role/tổ/activation tới commit, không chờ advisory lock `48683401` của A4 khi admin đang tạo hoặc sửa tài khoản khác. Cập nhật chính actor hoặc ghi catalog đồng thời vẫn có thể trả Conflict sau 2 giây chờ; đây là xung đột cần thiết để giữ quyền và dữ liệu nhất quán.

Duyệt đồng thời: người thắng commit quyết định. Nếu Approved thắng, reviewer còn lại vẫn nhìn thấy câu và nhận 409 InvalidStateTransition. Nếu Rejected thắng, câu trở lại phạm vi riêng tư của tác giả; reviewer khác nhận 404 NotFound trước kiểm trạng thái. Chỉ có một audit quyết định thành công. SQLSTATE lock/deadlock/FK/unique được chuyển thành lỗi an toàn. Audit lỗi làm rollback cả trạng thái, nội dung hoặc lệnh DELETE đã chạy. Log server chỉ ghi loại exception, SQLSTATE và stack, bỏ raw message.

Khi đề submit, trigger A2 giữ FOR SHARE trên câu nguồn; sửa nguồn giữ FOR UPDATE nên hai thao tác được tuần tự hóa. Snapshot đã đóng băng không bị sửa khi câu gốc trở về Draft/revision mới. A6 không tạo snapshot hoặc tính hash thay A7.

## Kiểm thử và chạy local

Tại gốc repo, với PostgreSQL có quyền CREATEDB:

```powershell
$env:A2_TEST_PG_ADMIN = Read-Host "Nhap connection string PostgreSQL test co quyen CREATEDB"
dotnet test backend/tests/ExamMaker.Api.Tests/ExamMaker.Api.Tests.csproj -c Release -m:1
dotnet test backend/tests/ExamMaker.Infrastructure.Tests/ExamMaker.Infrastructure.Tests.csproj -c Release -m:1
```

Fixture tạo DB tên ngẫu nhiên rồi xóa sau test, không sửa DB phát triển. Không lưu token, connection string hoặc dữ liệu thật. Để chạy toàn backend ở terminal có NuGet:

```powershell
dotnet test backend/ExamMaker.slnx -c Release -m:1
dotnet format backend/ExamMaker.slnx --verify-no-changes --no-restore
```

Test gồm bảng 30 cặp trạng thái/hành động, ma trận quyền đọc/duyệt/archive, payload, HTTP/JWT/ProblemDetails, review queue, revision, AI review flags, xóa có tham chiếu, snapshot không đổi, hai quyết định đồng thời, thu hồi quyền khi chờ pg_locks và audit rollback.

## Kết quả kiểm tra hiện tại

- Kiểm tra local ngày 21/11/2026: toàn solution backend Release **487/487 test pass, 0 failed, 0 skipped** (319 API/quy tắc, 38 Application, 130 Infrastructure), gồm các integration test PostgreSQL thật.
- Test xác nhận: sửa stem/payload/difficulty/explanation và archive bị chặn khi còn đề Draft/Rejected/Submitted của người khác; PUT không đổi nội dung vẫn thành công. Đề Approved/Published/Archived giữ nguyên snapshot khi nguồn bị archive. Head đã chuyển tổ vẫn archive được câu của mình. Các test quyền sau chờ khóa, audit rollback, payload và đồng thời tiếp tục pass.
- Schema TypeScript đã sinh lại và kiểm đồng bộ OpenAPI đạt. Formatter whitespace và `git diff --check` đạt. Kết quả CI của commit mới cần xác nhận sau khi push.
- Có test hồi quy HTTP cho key lỗi Required/binding (kể cả `$.grade`), test rubric cực nhỏ/zero/số âm và test PostgreSQL cho actor bị khóa hoặc không tồn tại gửi payload sai. Validation nghiệp vụ của Create chỉ chạy sau actor/role; toàn bộ test các vòng trước tiếp tục đạt.
- Có test stale approve/reject sau withdraw/sửa/resubmit giữ nguyên revision, token thời gian khác múi giờ, 400 trước overflow jsonb và lưu được rubric 1e-30. HTTP xác nhận body thiếu token bị chặn và references không lộ nội dung/email; catalog/admin user/AI đều dùng errors key camelCase.

## Kiểm tra trước merge

PR #69 dùng nhánh `feat/a6-question-workflow`, đích `dev`. Giữ A6 chưa tick tới khi review/CI đạt và PR được merge.

Thử thủ công bằng teacher/head/admin: tạo → submit → reject → sửa → submit → approve → sửa Approved không bị đề Draft/Rejected/Submitted tham chiếu → kiểm revision → submit/approve → archive. Kiểm tự duyệt, head khác tổ, head archive câu của mình sau chuyển tổ, teacher vào review queue và sửa/archive/xóa câu được đề tham chiếu; đối chiếu mã lỗi với OpenAPI.
