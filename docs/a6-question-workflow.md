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
| `DELETE /questions/{id}` | Xóa câu chưa có liên kết đề/bộ câu | Creator teacher/head; Draft/Rejected |
| `POST /questions/{id}/submit` | Gửi duyệt | Creator; Draft/Rejected |
| `POST /questions/{id}/withdraw` | Rút lại | Creator; Submitted |
| `POST /questions/{id}/approve` | Duyệt; body note tùy chọn, có thể không gửi body | Head cùng tổ hoặc admin, cấm tự duyệt |
| `POST /questions/{id}/reject` | Từ chối; body `{note}` bắt buộc | Cùng quyền approve |
| `POST /questions/{id}/archive` | Approved → Archived, không khôi phục | Teacher sở hữu; head cùng tổ; admin |
| `POST /questions/{id}/mark-reviewed` | Đặt aiReviewedByAuthor=true | Creator teacher/head; AI Draft/Rejected |
| `GET /review/questions` | Submitted theo submittedAt tăng dần, loại câu của mình | Head trong tổ; admin tất cả |

Danh sách ngân hàng sắp createdAt giảm dần, thêm id để thứ tự ổn định. Hàng đợi thêm id sau submittedAt. Mặc định page=1/pageSize=20; tối đa 100; tổng trang bằng 0 khi không có kết quả, trang vượt dữ liệu có items rỗng. Ngân hàng hỗ trợ status, subjectId, topicId, grade, type, difficulty, mine, aiJobId; hàng đợi chỉ hỗ trợ subjectId/topicId và phân trang. Count và lấy trang là hai truy vấn, có thể phản ánh hai thời điểm gần nhau khi có ghi đồng thời.

## Các hàm có tác dụng gì?

Code dùng comment kỹ thuật tiếng Anh theo quy ước dự án. Giải thích tiếng Việt:

| Hàm / thành phần | Tác dụng |
|---|---|
| `QuestionRules.CanRead` | Kiểm quyền đọc theo role, trạng thái, tổ và người tạo; dùng cho GET chi tiết. |
| `QuestionRules.RequireOwner` | Chặn thao tác lên nội dung của người khác và chặn admin tạo/sửa nội dung. |
| `QuestionRules.Validate` | Kiểm input câu hỏi và gọi kiểm payload theo dạng trước khi lưu/gửi duyệt. |
| `QuestionPayloadRules.Validate` | Kiểm schema đúng type; không đoán theo hình dạng JSON. |
| `Object/Labels/Text/Array` | Chặn field lạ/trùng/thiếu, nhãn trùng, boolean sai kiểu, câu trả lời rỗng và rubric điểm không hợp lệ. |
| `QuestionRules.Edit` | So nội dung thật sự; Approved đổi nội dung → Draft, revision+1; reset kiểm chứng/rà soát AI khi sửa. |
| `QuestionRules.Transition` | State machine thuần: kiểm quyền trước, sau đó chuyển trạng thái, thời điểm và quyết định duyệt. Không gọi DB/AWS. |
| `QuestionManagement.ActorAsync` | Đọc lại trạng thái/role/tổ từ DB; không tin dữ liệu quyền trong body/token cache. |
| `CatalogAsync` | Kiểm bộ topic/subject/grade và lấy department từ catalog, giữ department câu cố định khi sửa. |
| `MutateAsync` | Mở transaction, lấy khóa, kiểm actor, thực hiện thay đổi, lưu audit và commit cùng nhau. |
| `LockQuestionAsync` | Giữ FOR UPDATE và reload trước khi quyết định để hai reviewer không ghi đè kết quả. |
| `CreateAsync/UpdateAsync` | Tạo hoặc sửa câu theo quyền và ràng buộc danh mục; không cho client giả mạo source/creator/revision/status. |
| `TransitionAsync` | Đọc câu có khóa, gọi state machine ở Application và ghi audit từ trạng thái cũ sang mới. |
| `DeleteAsync` | Kiểm câu Draft/Rejected thuộc tác giả, kiểm liên kết, xóa bằng PostgreSQL và lưu lịch sử. |
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
- AI chưa rà soát không submit được: 409 `AiReviewRequired`. Sửa nội dung AI reset aiReviewedByAuthor, aiVerification và aiVerificationNote, giữ nguyên job/ordinal/source.
- Submit xóa quyết định cũ và đặt submittedAt mới. Withdraw xóa submittedAt và quyết định duyệt. Reject note không trắng, tối đa 2000; approve note tùy chọn, tối đa 2000. Reviewer và thời gian lưu UTC.
- 403 `SelfReview` riêng cho tự duyệt của head/admin; các lỗi quyền khác `Forbidden`. Sai trạng thái trả 409 `InvalidStateTransition`.
- Câu bị exam_version_questions hoặc question_set_items tham chiếu trả 409 `QuestionReferenced`; không tự xóa liên kết hoặc snapshot. Khi dùng bộ câu, phải gỡ liên kết trước như quy tắc A2.
- Validation trả 400 `ValidationFailed` với errors[field]; body không cho field ngoài hợp đồng. Bốn dạng payload đều kiểm: MC đúng 4 nhãn A–D/1 correctLabel; TrueFalse 4 nhãn a–d và boolean; ShortAnswer có đáp án, tolerance chỉ cho number; Essay có rubric điểm dương. Tổng rubric bằng điểm câu kiểm khi ghép đề ở A7.

## Transaction và đồng thời

Mỗi ghi giữ advisory lock quản trị `48683401`, sau đó đọc actor live, giữ shared catalog lock `48683402` rồi khóa hàng câu hỏi. Thứ tự này phối hợp với A4/A5; A4 đồng bộ Cognito có thể khiến A6 trả Conflict sau 2 giây chờ khóa. Mức khóa chung phù hợp demo, chưa tối ưu thông lượng lớn. Role/tổ bị thay đổi trong lúc chờ khóa sẽ được kiểm lại sau khi có khóa.

Duyệt đồng thời: người thắng commit quyết định; người còn lại đọc trạng thái mới rồi nhận InvalidStateTransition. SQLSTATE lock/deadlock/FK/unique được chuyển thành lỗi an toàn. Audit lỗi làm rollback cả trạng thái, nội dung hoặc lệnh DELETE đã chạy. Log server chỉ ghi loại exception, SQLSTATE và stack, bỏ raw message.

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

## Kết quả local 20/11/2026

- **259/259 API/quy tắc** và **113/113 Infrastructure** pass, không skip; gồm **77 PostgreSQL** thật.
- A6 thêm **150 test**: 125 ca quy tắc/payload, 14 HTTP và 11 PostgreSQL. Các test quy tắc thuần nằm trong project API.Tests cùng nhóm policy A3 hiện có, không cần AWS hoặc DB.
- Build API Release đạt, không warning/error. Whitespace và `git diff --check` đạt.
- Chưa chạy lại được project Application.Tests hiện có của B5 vì thiếu `Microsoft.Extensions.TimeProvider.Testing` và NuGet bị chặn trong môi trường local này. Không coi hai project đã pass là toàn bộ solution; CI phải chạy đủ ba project test. Formatter toàn solution cần terminal/CI vì build host named pipe bị giới hạn trong môi trường agent.

## Git và kiểm tra trước merge

Làm trên nhánh `feat/a6-question-workflow` để tách code A6 khỏi PR A5. Nếu A5 chưa merge, A6 hiện dựa trên nhánh A5; sau khi A5 merge cần đồng bộ/rebase vào dev trước khi mở PR A6. Không đánh dấu A6 hoàn tất trước review/CI/merge.

Thử thủ công bằng tài khoản teacher/head/admin: tạo → submit → reject → sửa → submit → approve → sửa Approved → kiểm revision → submit/approve → archive. Thử tự duyệt, head khác tổ, teacher vào review queue và xóa câu đã được đề tham chiếu; đối chiếu mã lỗi với bảng trên.
