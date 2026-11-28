# A5 — Quản lý danh mục tổ, môn và chương

A5 cung cấp API đọc danh mục cho người dùng đã đăng nhập và API quản trị cho admin. Dùng schema/seed A2 hiện có, không cần migration mới hoặc cấu hình AWS riêng.

## API

Các đường dẫn dưới đây có tiền tố `/api/v1`.

| Đường dẫn | Phương thức | Tác dụng | Quyền |
|---|---|---|---|
| `/departments` | GET | Danh sách tổ theo tên | Teacher, head, admin |
| `/subjects` | GET | Danh sách môn; lọc `departmentId` | Teacher, head, admin |
| `/topics` | GET | Danh sách chương; lọc `subjectId`, `grade`; thứ tự khối, sortOrder | Teacher, head, admin |
| `/admin/departments` | POST | Tạo tổ, body `{name}` | Admin |
| `/admin/departments/{id}` | PUT / DELETE | Sửa / xóa tổ | Admin |
| `/admin/subjects` | POST | Tạo môn, body `{name, departmentId}` | Admin |
| `/admin/subjects/{id}` | PUT / DELETE | Sửa / xóa môn | Admin |
| `/admin/topics` | POST | Tạo chương, body `{subjectId, grade, name, sortOrder}` | Admin |
| `/admin/topics/{id}` | PUT / DELETE | Sửa / xóa chương | Admin |

PUT thay toàn bộ các trường như POST. POST trả 201 và đối tượng đã tạo, PUT trả 200, DELETE trả 204. Không bổ sung endpoint GET chi tiết ngoài OpenAPI. Danh sách trả `page`, `pageSize`, `totalItems`, `totalPages`, `items`; mặc định trang 1, 20 phần tử, tối đa 100 phần tử mỗi trang. Trang vượt dữ liệu trả items rỗng, giữ tổng số phần tử khớp bộ lọc. Bộ lọc parent không tồn tại trả trang rỗng.

Tên được trim và chuẩn hóa Unicode NFC, từ chối ký tự điều khiển (kể cả tab/xuống dòng); bắt buộc có nội dung, tối đa 200 ký tự với tổ/môn, 300 với chương. Parent phải tồn tại, grade thuộc 10/11/12 và sortOrder >= 0. Độ dài kiểm sau trim/NFC ở service, DTO chỉ kiểm bắt buộc để không từ chối tên hợp lệ có khoảng trắng ngoài giới hạn. Trường bắt buộc bị thiếu/null, GUID rỗng, thuộc tính body không được hỗ trợ hoặc offset phân trang vượt int trả 400 `ValidationFailed`.

## Các hàm có tác dụng gì?

Comment kỹ thuật trong code dùng tiếng Anh theo quy ước dự án. Bảng này giải thích bằng tiếng Việt:

| Thành phần / hàm | Tác dụng |
|---|---|
| `CatalogController.Departments/Subjects/Topics` | Nhận tham số HTTP và trả danh sách đúng schema; middleware kiểm người gọi đã đăng nhập. |
| `Admin*Controller.Create/Update/Delete` | Chặn người không phải admin, lấy actor từ phiên đăng nhập; không nhận actor từ body. |
| `ICatalogManagement` | Hợp đồng use case giữa API và Infrastructure; giúp thay implementation khi test. |
| `CatalogInputRules.Name/Parent/Query` | Trim/NFC tên, từ chối ký tự điều khiển, kiểm parent GUID và phân trang/bộ lọc trước khi ghi hoặc truy vấn. |
| `CatalogManagement.DepartmentsAsync/SubjectsAsync/TopicsAsync` | Dùng PostgreSQL lọc, sắp xếp ổn định rồi chuyển sang DTO, không trả entity/navigation. |
| `PageAsync` | Kiểm phân trang, đếm tổng kết quả và chỉ lấy trang yêu cầu. Count và lấy trang là hai câu truy vấn; khi có ghi đồng thời, tổng và items có thể phản ánh hai thời điểm gần nhau. |
| `SaveDepartmentAsync` | Tạo hoặc đổi tên tổ, ghi audit trước/sau. |
| `SaveSubjectAsync` | Kiểm tổ tồn tại, tạo/sửa môn; FK ngăn chuyển tổ khi làm sai tham chiếu câu hỏi/đề. |
| `SaveTopicAsync` | Kiểm môn, khối, thứ tự rồi tạo/sửa chương; FK ngăn đổi môn/khối khi câu hỏi đang tham chiếu. |
| `DeleteDepartmentAsync/DeleteSubjectAsync/DeleteTopicAsync` | Xóa danh mục chưa được dùng; dữ liệu tham chiếu được giữ bằng FK RESTRICT. |
| `WriteAsync` | Mở transaction, lấy khóa chung với A4, kiểm lại admin active, thực hiện lệnh và commit cả audit. Lỗi sẽ rollback; chờ khóa tối đa 2 giây. |
| `Audit` | Lưu loại thực thể, id, actor, hành động và metadata trước/sau trong cùng transaction. |
| `CatalogExceptionHandler.TryHandleAsync` | Trả ProblemDetails với code/traceId an toàn; không trả SQL hoặc nội dung lỗi PostgreSQL cho client. |

## Sửa review A5: job AI và audit xóa

Job `Queued`/`Running` còn giữ tham chiếu danh mục trong `params` dù chưa có câu hỏi để FK bảo vệ. A5 trả 409 khi xóa môn/chương hoặc đổi tổ của môn, đổi môn/khối của chương mà job đang sử dụng. Đổi tên/thứ tự vẫn được phép. Job `Succeeded`/`Failed` không chặn danh mục; câu hỏi/đề đã sinh vẫn được FK RESTRICT bảo vệ.

A5 giữ exclusive advisory transaction lock `48683402`; `EfAiJobStore` giữ shared lock cùng khóa từ lúc kiểm lại topic/subject/grade cho tới commit job. Các yêu cầu tạo job vẫn chạy song song với nhau. Nếu danh mục thay đổi sau validation ban đầu của B5, trả 400 ValidationFailed, không giữ quota và không enqueue. Nếu job commit trước, A5 nhìn thấy job mới và trả 409. Worker B6 phải giữ trạng thái Queued/Running đến khi lưu câu hỏi và chuyển trạng thái terminal trong cùng transaction theo context.

Audit DELETE đọc metadata trước khi xóa: tên tổ; tên môn và departmentId; tên chương, subjectId, grade, sortOrder. Metadata và lệnh xóa được commit cùng transaction.

Endpoint list dùng DTO query riêng, bỏ qua query key ngoài hợp đồng: `/departments` chỉ nhận phân trang; `/subjects` nhận thêm departmentId; `/topics` nhận thêm subjectId/grade. Bộ lọc được hỗ trợ nhưng sai kiểu/giá trị vẫn trả 400.

A4 có thể giữ khóa quản trị `48683401` tới khoảng 20 giây trong khi đồng bộ Cognito. A5 dùng chung khóa để kiểm quyền an toàn, nên có thể trả 409 “đang bận” sau 2 giây khi A4 đang chạy. Chấp nhận đánh đổi ở quy mô demo; tải lại dữ liệu và thử sau, không tự retry vòng lặp.

Test race kiểm `pg_locks` theo PID cho đến khi thấy tiến trình thật sự chờ đúng advisory lock rồi mới commit bên giữ lock; không dựa vào thời gian delay cố định.

## Ràng buộc và xử lý lỗi

- Chưa đăng nhập: 401; teacher/head ghi danh mục: 403 `Forbidden`. Service còn kiểm lại admin trong DB sau khi giữ khóa để tránh dùng quyền đã bị thu hồi.
- Bản ghi PUT/DELETE không tồn tại: 404 `NotFound`; không tự tạo mới khi PUT, không coi DELETE lặp là thành công.
- Unique: tên tổ toàn cục; tên môn trong tổ; tên chương trong môn/khối. Giữ phân biệt hoa/thường theo index hiện có, không thêm quy tắc lowercase.
- Tên trùng, danh mục đang được tham chiếu hoặc khóa ghi bận: 409 `Conflict`. Không tự xóa phụ thuộc hoặc tự chuyển câu hỏi/đề.
- Khóa advisory dùng chung A4/A5 giữ nhất quán giữa cập nhật user và kiểm quyền admin. Chờ khóa tối đa 2 giây; không gọi Cognito khi đang làm danh mục.
- Cập nhật cột alternate key dùng `ExecuteUpdateAsync` để tránh EF cấm sửa khóa đang tracking. PostgreSQL tiếp tục kiểm composite FK. Audit lỗi làm rollback cả câu UPDATE đã thực thi.
- Lỗi PostgreSQL ghi loại exception, SQLSTATE và stack phía server; bỏ raw message để tránh ghi dữ liệu đầu vào vào log. Lỗi commit không rõ kết quả cần tải lại danh mục/audit trước khi thử lại POST.

## Chạy kiểm thử

Trong PowerShell tại gốc repository, dùng PostgreSQL local có quyền CREATEDB:

```powershell
$env:A2_TEST_PG_ADMIN = Read-Host "Nhap connection string PostgreSQL test co quyen CREATEDB"
dotnet test backend/ExamMaker.slnx -c Release -m:1 --filter FullyQualifiedName~Catalog
```

Fixture tạo database tên ngẫu nhiên rồi xóa sau test, không sửa database phát triển. Nếu thiếu biến môi trường, test PostgreSQL local sẽ skip; CI bắt buộc có cấu hình này. Không lưu connection string hoặc token vào repo.

Test A5 bao gồm HTTP/JWT/policy, payload bắt buộc, DTO/phân trang/lọc, CRUD thật trên PostgreSQL, audit, unique theo phạm vi, FK chặn xóa/chuyển, quyền admin thay đổi khi chờ khóa, timeout khóa và rollback khi audit lỗi.

## Kết quả local ban đầu, trước khi tích hợp B5 (20/11/2026)

- Toàn backend Release: **162/162 test pass, không skip** — 96 API, 66 Infrastructure; gồm 40 test PostgreSQL thật.
- A5 bổ sung 27 test: 17 HTTP và 10 PostgreSQL, bao gồm thu hồi quyền và audit rollback.
- Build solution Release đạt; kiểm whitespace trên các file C# thay đổi và `git diff --check` đạt.
- Lệnh `dotnet format backend/ExamMaker.slnx --verify-no-changes --no-restore` không thực thi được trong môi trường agent vì build host bị từ chối truy cập named pipe. Cần chạy formatter đầy đủ ở terminal thông thường/CI trước merge; không coi kiểm whitespace là thay thế toàn bộ formatter.
- Đây là kết quả của lượt triển khai ban đầu; kết quả sửa review sau khi tích hợp B5 được ghi bên dưới. Checklist task vẫn để mở đến khi bản sửa được review, CI xanh và merge vào dev.

## Kiểm thử bản sửa review sau khi tích hợp B5

- API Release: **120/120 pass, không skip**, gồm HTTP ValidationFailed khi catalog đổi giữa validation và commit, query key ngoài hợp đồng và độ dài tên trước/sau trim.

- Infrastructure Release: **102/102 pass, không skip**, gồm **66 test PostgreSQL thật**; đã chạy các ca job Queued/Running chặn xóa/chuyển, terminal job cho phép thay đổi, audit xóa giữ metadata, NFC unique và race hai chiều quan sát bằng pg_locks.
- Project `ExamMaker.Application.Tests` chưa chạy được local vì thiếu package `Microsoft.Extensions.TimeProvider.Testing`, NuGet bị từ chối truy cập trong môi trường agent. Không coi kết quả chạy solution với `--no-restore` là đã chạy project này. Đã thêm test unit cho lỗi catalog thay đổi, cần CI chạy lại đầy đủ; nhánh xử lý này cũng có test qua HTTP ở project API.
- Kiểm whitespace và `git diff --check` được chạy riêng; formatter toàn solution cần terminal thông thường/CI vì hạn chế named pipe đã ghi ở trên.

## Kiểm tra thủ công trước merge

1. Chạy API với cấu hình A3 và DB local; lấy access token qua luồng đăng nhập hiện có.
2. Teacher/head gọi GET danh mục phải 200, POST/PUT/DELETE admin phải 403; bỏ token phải 401.
3. Với admin, tạo tổ → môn → chương bằng dữ liệu giả; đổi tên, lọc GET và kiểm trường trả về.
4. Thử trùng tên, xóa tổ có môn, xóa môn có chương: 409; xóa chương → môn → tổ không còn phụ thuộc: 204.
5. Kiểm audit có đúng actor và hành động, không có audit thành công cho thao tác lỗi.
6. Mở PR vào dev, chờ Backend/Secret scan và các check bắt buộc xanh; review/merge trước khi tick A5 hoàn tất.
