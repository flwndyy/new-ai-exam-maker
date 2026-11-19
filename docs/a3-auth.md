# A3 — Xác thực và phân quyền backend

## Luồng xử lý

1. Client gửi `Authorization: Bearer <access token>`.
2. JwtBearer lấy discovery/JWKS của User Pool qua HTTPS, kiểm chữ ký RS256, issuer, thời hạn và client_id.
3. `CognitoBearerEvents.TokenValidated` yêu cầu token_use=access và sub hợp lệ. ID token không dùng gọi API.
4. `DatabaseUserDirectory.FindBySubjectAsync` tìm tài khoản theo cognito_sub, đọc quyền và trạng thái mới từ DB.
5. User không tồn tại/bị khóa/hồ sơ quyền không hợp lệ nhận 401. Không tự tạo tài khoản từ token.
6. Principal được tạo lại từ DB. Claim role/cognito:groups trong token không quyết định quyền ứng dụng.
7. Policy kiểm role/tổ/ownership. `GET /api/v1/me` trả hồ sơ theo schema Me trong OpenAPI.

Không có endpoint đăng nhập hoặc tự cấp JWT trong API. Frontend B2 đăng nhập Cognito bằng luồng đã chốt; A4 tạo user và đồng bộ tài khoản.

## Tác dụng các lớp và hàm

| File/hàm | Tác dụng |
|---|---|
| `CognitoOptions.HasValidValues` | Kiểm Region, UserPoolId, ClientId trước khi tạo địa chỉ discovery; lớp này chỉ giữ cấu hình. |
| `AddExamMakerAuthentication` | Đăng ký JwtBearer, user theo request và các policy dùng chung. |
| `ConfigureBearer` / `HasExpectedClient` | Bật kiểm chữ ký/hạn dùng/issuer và đối chiếu client_id. |
| `MessageReceived` | Từ chối token nếu Cognito chưa được cấu hình. |
| `TokenValidated` | Kiểm access token, tìm user DB, tạo principal nội bộ và ghi CurrentUser. |
| `Challenge` / `Forbidden` | Trả lỗi 401/403 dạng ProblemDetails với code/traceId; 401 có Bearer challenge. |
| `DatabaseUserDirectory.FindBySubjectAsync` | Đọc quyền mới; chỉ cache email/họ tên/tên tổ một phút. |
| `CurrentUser.SetUser` | Giữ hồ sơ đã xác thực trong phạm vi một request, không chia sẻ giữa các user. |
| `ContentAccessHandler.HandleRequirementAsync` | Kiểm role, cùng tổ, chủ sở hữu và cấm tự duyệt. |
| `MeController.Get` | Trả id/email/fullName/role/departmentId/departmentName/isActive; không trả token/sub. |

Comment giải thích hàm trong phần A3 viết bằng tiếng Việt theo yêu cầu người dùng. Tên lớp/hàm/biến giữ tiếng Anh.

## Cấu hình chạy thật

Lấy ba giá trị từ người phụ trách Cognito, không ghi giá trị thật vào repo:

| Biến môi trường | Giá trị |
|---|---|
| `Cognito__Region` | Vùng AWS của User Pool |
| `Cognito__UserPoolId` | ID User Pool cùng vùng |
| `Cognito__ClientId` | App Client dùng cho frontend |
| `ConnectionStrings__ExamMaker` | Kết nối PostgreSQL đã có migration A2 |

Có thể nạp cùng nhóm cấu hình từ Parameter Store theo cơ chế A1. Xác thực JWT không cần client secret hoặc AWS access key vì JWKS là khóa công khai.

Trong Developer PowerShell, từ thư mục `backend`, thay placeholder bằng giá trị thật:

```powershell
$env:Cognito__Region = "ap-southeast-1"
$env:Cognito__UserPoolId = "<USER_POOL_ID>"
$env:Cognito__ClientId = "<APP_CLIENT_ID>"
dotnet run --project src/ExamMaker.Api --no-launch-profile --urls http://localhost:5080
```

Phải đặt `ConnectionStrings__ExamMaker` trong cùng terminal trước khi gọi endpoint có xác thực. Đừng gửi token/connection string hoặc dữ liệu thật vào chat, ảnh công khai hay Git.

Khi hoàn toàn chưa cấu hình Cognito, `/health` vẫn trả 200 để kiểm tra host; API bảo vệ trả 401. Đây không phải xác nhận ứng dụng đã sẵn sàng xác thực. Cấu hình Cognito một phần hoặc sai định dạng làm startup thất bại. Triển khai thật phải cấp đủ cấu hình và kết nối ra discovery/JWKS qua HTTPS.

## Policy cho các task tiếp theo

| Policy | Quy tắc |
|---|---|
| `Admin` | Chỉ admin |
| `ContentAuthor` | Teacher hoặc head; admin không tạo nội dung |
| `SameDepartment` | Cùng tổ hoặc admin |
| `Owner` | Teacher/head là chủ sở hữu |
| `Reviewer` | Head cùng tổ hoặc admin; luôn cấm tự duyệt |

Role policy dùng `[Authorize(Policy = AccessPolicies.Admin)]` hoặc ContentAuthor. Resource policy dùng `IAuthorizationService.AuthorizeAsync(User, resource, policyName)` với `ContentAccessResource` lấy creator/department từ DB. Không truyền resource là HttpContext hoặc lấy các ID quyền từ body client.

Các policy này là thành phần dùng chung, không thay thế kiểm tra trạng thái/nội dung của use case. Ví dụ SameDepartment không tự cho phép đọc câu Draft của người khác. Khi ghép đề, Application vẫn phải kiểm tra môn/khối/tổ và trạng thái theo context.md. Không tự duyệt áp dụng cả admin.

Quyền và trạng thái luôn đọc DB ở request mới; cache một phút chỉ làm chậm cập nhật các trường hiển thị. Vô hiệu hóa user hoặc đổi role/tổ không phải chờ hết token/cache. JWT offline không tự kiểm tra danh sách thu hồi của Cognito; A4 phải cập nhật DB khi khóa tài khoản như mục 3.3.

## Kiểm thử

Từ `backend`, để chạy cả test PostgreSQL trên server local có quyền CREATEDB:

```powershell
$env:A2_TEST_PG_ADMIN = $env:ConnectionStrings__ExamMaker
dotnet test ExamMaker.slnx -m:1
```

Test dùng database tạm; không sửa DB phát triển. CI đã có PostgreSQL service. Nếu thiếu A2_TEST_PG_ADMIN ở local, test DB được đánh dấu skip; xem số skipped để biết có thực sự kiểm DB chưa.

- Test HTTP ký token RSA tạm, dùng pipeline JWT và policy thật; discovery và lookup user được thay trong test host.
- Kiểm token sai chữ ký/issuer/client, hết hạn/chưa có hiệu lực, thiếu exp/sub/token_use/client_id, claim trùng, ID token, token unsigned/sai thuật toán.
- Kiểm 401/403/500, schema Me, group admin giả trong token, thay đổi quyền/khóa user và scope request độc lập.
- Test policy kiểm ma trận role/tổ/ownership/cấm tự duyệt.
- Hai test PostgreSQL kiểm lookup thật, thay đổi role/tổ/trạng thái dù cache đang có, xóa user, không cache user chưa tồn tại và admin không có tổ.

Smoke test với Cognito thật cần User Pool/App Client, access token hợp lệ và user nội bộ đã được bootstrap/A4 tạo. Đối chiếu `/me`, rồi khóa user trong DB và gọi lại cùng token để xác nhận 401. Không coi test ký token cục bộ là smoke test AWS.

## Tài liệu chính thức

- [AWS — Verify Cognito JWT](https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-tokens-verifying-a-jwt.html)
- [Microsoft — JWT bearer trong ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
