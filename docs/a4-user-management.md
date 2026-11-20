# A4 — Quản lý user và đồng bộ Cognito

## Chức năng

Các endpoint bắt đầu bằng `/api/v1/admin/users`, chỉ admin được gọi:

| Method và đường dẫn | Tác dụng |
|---|---|
| GET `/` | Danh sách phân trang; lọc role, departmentId, isActive và q (email/họ tên) |
| GET `/{id}` | Chi tiết user, có createdAt; không chứa mật khẩu/token |
| POST `/` | Tạo tài khoản Cognito, gửi lời mời mật khẩu tạm, gán group và lưu DB |
| PUT `/{id}` | Đồng bộ email, tên, role/group, tổ và trạng thái |
| DELETE `/{id}` | Xóa mềm: khóa DB, Disable và GlobalSignOut Cognito; gọi lại được |
| POST `/reconcile` | Chỉ đọc, báo lệch role/trạng thái hoặc thiếu user hai phía |

Dùng đúng DTO trong `docs/api/openapi.yaml`. Tạo user không nhận mật khẩu. Cập nhật phải có isActive, không cho thiếu trường này tự biến thành false. Teacher/head bắt buộc có tổ; admin có thể không có tổ. Email được trim/lowercase, tên được trim. Admin không được tự khóa hoặc tự hạ vai trò.

## Tác dụng lớp và hàm

Comment kỹ thuật trong code bằng tiếng Anh theo quy tắc chung; bảng này giải thích tiếng Việt.

| Lớp/hàm | Tác dụng |
|---|---|
| `UserInputRules.Normalize` | Kiểm email/tên/role/tổ, chuẩn hóa trước khi gọi DB hoặc AWS |
| `EnsureNotSelfLockout` | Chặn admin tự mất quyền quản trị |
| `IUserManagement` | Hợp đồng use case, giúp API không phụ thuộc trực tiếp SDK/EF |
| `ICognitoUserAdmin` | Tách các lệnh AWS để test lỗi và đền bù bằng fake |
| `UserManagementService.ListAsync/GetAsync` | Đọc hồ sơ DB, lọc và phân trang ổn định |
| `CreateCoreAsync` | Tạo Cognito + group + DB; khi lỗi thì đền bù tài khoản vừa tạo |
| `UpdateAsync/UpdateCoreAsync` | Kiểm quyền admin mới từ DB và đồng bộ profile/group/trạng thái |
| `PersistDisabledAsync` | Commit trạng thái khóa trước để token cũ bị từ chối ngay |
| `DeactivateAsync` | Khóa Cognito và thu hồi phiên; lỗi AWS không mở khóa DB lại |
| `Audit` | Ghi thao tác tạo/sửa/khóa user cùng transaction DB; bootstrap có actor NULL và note |
| `LockAsync` | Khóa transaction PostgreSQL dùng chung giữa các process để tuần tự hóa thao tác ghi |
| `CompensateAsync` | Khôi phục bằng cancellation token độc lập, timeout 30 giây, kể cả HTTP request đã hủy |
| `ReconcileAsync` | Báo RoleMismatch, DisabledMismatch, MissingInCognito, MissingInDb; không tự sửa |
| `BootstrapAsync` | Tạo admin đầu tiên khi DB chưa có bất kỳ admin nào, dùng qua CLI riêng |
| `CognitoUserAdmin.FindAsync` | Tìm theo sub bất biến và kiểm sub trả về khớp; không dùng email làm định danh cập nhật |
| `CognitoUserAdmin.SetProfileAsync` | Đồng bộ thuộc tính và đúng group ứng dụng; giữ group ngoài teacher/head/admin |
| `CognitoUserAdmin.ListAsync/GroupsAsync` | Đọc đầy đủ phân trang user/group của AWS |
| `UserOperationExceptionHandler` | Trả ProblemDetails với code/traceId, không lộ nội dung lỗi AWS/DB |
| `AddUserManagement` | Đăng ký service và AWS client lazy, lấy credential qua chuỗi mặc định |

## Migration

`AddUserAdministration` thêm `users.created_at` (timestamp with time zone, default now()). Thời gian user cũ là lúc migration, không phải lịch sử tạo thật. Migration tạo unique index `ux_users_email_lower` trên `lower(btrim(email))`, quản lý bằng SQL trong Up/Down vì EF không mô tả expression index này bằng thuộc tính Email trực tiếp.

Nếu email cũ trùng sau khi trim/lowercase thì migration thất bại nguyên khối. Phải rà soát dữ liệu và xử lý thủ công, không tự gộp hoặc xóa tài khoản. Truy vấn kiểm tra ở local/phiên quản trị riêng:

```sql
SELECT lower(btrim(email)), count(*)
FROM users
GROUP BY lower(btrim(email))
HAVING count(*) > 1;
```

Từ thư mục backend, đặt connection string đúng môi trường rồi chạy:

```powershell
dotnet ef database update --project src/ExamMaker.Infrastructure
```

Công cụ bootstrap không tự migrate và không in connection string.

## An toàn đồng bộ

DB là nguồn quyền thật. Tạo/sửa/khóa user ghi audit log cùng transaction với thay đổi DB; bootstrap là tác vụ hệ thống có note. Mỗi thao tác ghi kiểm lại actor còn active/admin sau khi giữ khóa. Chỉ xóa tài khoản Cognito do chính lần tạo thất bại đó tạo ra; UsernameExists không được xóa user cũ. Lời mời có thể đã gửi trước khi đền bù, khi đó tài khoản không còn đăng nhập được và cần cấp lời mời mới sau khi xử lý lỗi.

Khi sửa lỗi trước commit, khôi phục email/tên/email_verified và group cũ. Group ngoài ba role ứng dụng được giữ nguyên. Nếu commit DB mất kết nối và chưa rõ thành công hay thất bại, không tự xóa/đảo Cognito: chạy reconcile và kiểm tra cả DB/AWS trước khi quản trị viên xử lý. Lỗi cleanup được log thông điệp an toàn, trả InternalError; không nuốt lỗi thành kết quả thành công.

Khóa user commit DB trước rồi gọi AWS. Nếu AWS hoặc GlobalSignOut lỗi, DB vẫn false; retry DELETE để thử lại. Với PUT có isActive=false, trạng thái khóa cũng giữ nguyên khi profile/group đồng bộ lỗi. Nếu user được một thao tác khác mở khóa giữa hai giai đoạn, yêu cầu khóa cũ trả Conflict, không âm thầm khóa phía AWS trong khi DB đang mở. Bật lại user chỉ commit DB sau khi đồng bộ AWS thành công.

Admin phải xác nhận email mới là hợp lệ và thuộc người nhận trước khi đổi. API quản trị đánh dấu email_verified=true để cập nhật ngay theo quy định user pool, không để DB đổi email trong khi Cognito giữ email cũ chờ xác minh. Đây không phải luồng tự đổi email của giáo viên.

Reconcile không phải snapshot transaction xuyên AWS/DB: nếu đang có thao tác thay đổi đồng thời, chạy lại khi hệ thống yên. Sửa lệch phải xác minh theo sub, không nối hai tài khoản chỉ vì email giống nhau. Không thay role DB dựa vào group Cognito.

## Quyền AWS và cấu hình

Backend dùng `Cognito__Region`, `Cognito__UserPoolId`, `ConnectionStrings__ExamMaker` (và cấu hình A3 khi chạy API). Có thể nạp qua `ParameterStore__Path`. Không lưu giá trị thật vào source/appsettings, không dùng client secret.

AWS client dùng default credential chain: máy local dùng profile/phiên được cấp quyền; EC2 dùng instance role. Cần các quyền sau, giới hạn vào User Pool của team:

- `cognito-idp:AdminCreateUser`, `AdminDeleteUser`, `AdminGetUser`.
- `cognito-idp:AdminAddUserToGroup`, `AdminRemoveUserFromGroup`, `AdminListGroupsForUser`.
- `cognito-idp:AdminUpdateUserAttributes`, `AdminEnableUser`, `AdminDisableUser`, `AdminUserGlobalSignOut`.
- `cognito-idp:ListUsers`.

Bảo phải tạo sẵn ba group teacher/head/admin. Tài khoản Cognito teacher để đăng nhập ứng dụng khác với IAM principal gọi AWS Admin API; không lấy token teacher thay IAM credential. Không chạy test AWS thật với địa chỉ `.local` vì không thể nhận lời mời/khôi phục mật khẩu.

## Bootstrap admin đầu tiên

Chạy bởi người triển khai có quyền IAM ở trên. Công cụ từ chối khi đã có admin kể cả admin bị khóa; không dùng nó để tự nâng quyền tài khoản đang có.

Trong PowerShell đã cấu hình DB/Cognito đúng môi trường:

```powershell
$env:Bootstrap__Email = Read-Host "Email admin co the nhan thu"
$env:Bootstrap__FullName = Read-Host "Ho ten admin"
dotnet run --project tools/ExamMaker.Bootstrap
Remove-Item Env:Bootstrap__Email, Env:Bootstrap__FullName -ErrorAction SilentlyContinue
```

Cognito sinh mật khẩu tạm và gửi thư mời. Admin đăng nhập Managed Login và bắt buộc đổi mật khẩu trước khi dùng API. Không có endpoint bootstrap công khai, không đưa mật khẩu tạm hoặc token vào chat/log/Git. Không chạy bootstrap trong CI.

## Kiểm thử

Kết quả local 20/11/2026: **128/128 test Release pass, không skip** — 78 API và 50 Infrastructure, gồm 28 test PostgreSQL thật và 6 test adapter AWS dùng stub. Đã kiểm migration mới/nâng cấp/rollback/reapply và script idempotent qua bộ test PostgreSQL. Migration đã áp dụng thành công vào PostgreSQL phát triển local. Build toàn solution Release (gồm công cụ bootstrap) đạt 0 warning, 0 error; kiểm whitespace và git diff --check đạt. Full dotnet format theo solution bị môi trường chặn named pipe; cần xác nhận lại bằng CI hoặc terminal của người triển khai.


Test API ký JWT tạm để kiểm admin/teacher/head/anonymous, DTO, phân trang, actor lấy từ request scope và mã lỗi. Test adapter AWS dùng stub SDK để kiểm request, phân trang, group ngoài ứng dụng và sub mismatch. Test PostgreSQL dùng DB tạm để kiểm transaction, timestamp, unique email, tạo trùng đồng thời, bootstrap, đền bù/hủy request, fail-closed, đối soát và retry.

```powershell
$env:A2_TEST_PG_ADMIN = Read-Host "Ket noi PostgreSQL test co quyen CREATEDB"
dotnet test ExamMaker.slnx -c Release -m:1
```

Đặt `A2_TEST_PG_ADMIN` bằng kết nối PostgreSQL có quyền tạo DB tạm để chạy integration test; thiếu biến này local sẽ skip. CI dùng PostgreSQL service và bắt buộc chạy test DB. Các test tự động không gọi Cognito thật, không gửi thư và không tạo user trên AWS.

Chưa xác nhận A4 trên AWS thật. Sau khi CI xanh và review code, người triển khai bootstrap/test admin rồi thử tạo user bằng email nhận được thư, đổi mật khẩu lần đầu, đổi role/tổ/email, khóa/mở khóa bằng API, teacher nhận 403 và reconcile chỉ đọc. Các bước có thể gửi email thật nên phải chọn tài khoản nhận thư đã được đồng ý trước.
