# A7 — Phiên bản đề, snapshot và quy trình duyệt

A7 dùng schema A2 hiện có; không thêm migration, không gọi AWS. Bao gồm API identity/composition dùng chung với B9 để frontend soạn đề sử dụng trực tiếp, tránh tạo controller trùng ở B9. Xuất đề học sinh/đáp án vẫn thuộc B10.

## Hành vi

- `POST /exams` tạo đề và Draft v1 rỗng, thời gian 45 phút. Môn/khối/tổ cố định, chỉ đổi tiêu đề qua PUT. Admin không tạo nội dung.
- `PUT /exam-versions/{vid}` thay toàn bộ danh sách có thứ tự và thời gian 1–600 phút. Tối đa 200 câu, không trùng nguồn, điểm dương biểu diễn bằng .NET decimal; tổng phải nằm trong phạm vi decimal. Draft được rỗng, Submit thì không. `totalPoints` luôn do server tính.
- Nguồn phải là câu của tác giả hoặc Approved trong tổ cố định của đề, đúng môn/khối/tổ đề, không Archived. Tác giả chuyển tổ vẫn ghép/clone/submit đề cũ trong phạm vi đề đó. Giữ khóa nguồn tới commit để không lưu liên kết sau một archive hoặc đổi phân loại đồng thời.
- Submit chỉ nhận mọi nguồn Approved, sao chép type/difficulty/stem/payload/explanation, điểm/thứ tự và source revision. SHA-256 gồm duration và các item sắp theo thứ tự; object trong payload sắp key đệ quy. Điểm chuẩn hóa scale decimal. Không đưa thời gian, người duyệt hoặc tên hiển thị vào hash.
- `Draft/Rejected → Submitted → Approved → Published`; submit lại từ Rejected chụp lại snapshot. Tác giả được withdraw Submitted về Draft; xóa hash/snapshot/quyết định cũ. Reject cần note. Archive chỉ Approved/Rejected và là trạng thái cuối.
- Approve/reject nhận `submittedAt` của lượt reviewer đã đọc. So UTC theo mili giây để hỗ trợ JavaScript Date; lượt mới luôn tăng so với sự kiện trước. Thiếu token trả 400 `errors[submittedAt]` sau kiểm quyền/trạng thái, token cũ trả 409 `Conflict`; không tự retry bằng token mới. Lỗi binding JSON do MVC xử lý trước action.
- Publish/unpublish chỉ admin. Bản Published khác trong cùng đề gây 409 `PublishedVersionExists`; phải unpublish bản cũ trước. Unpublish giữ nguyên snapshot/hash và thông tin publish để đối chiếu audit.
- `POST /exams/{id}/versions` sao chép từ `fromVersionId` trong cùng đề (mặc định bản mới nhất). Nếu còn Draft thì 409; số phiên bản cấp tuần tự dưới khóa Exam. Bản mới dùng liên kết/revision nguồn hiện tại, bỏ câu Archived hoặc không còn chọn được với warning `SourceOmitted`. Snapshot cũ giữ nguyên.
- Archive Rejected giữ nguyên dữ liệu đang có theo A2, không chụp lại nguồn hoặc tính hash mới. Nội dung snapshot thiếu trả null kèm warning `SnapshotUnavailable`, không lấy nguồn hiện tại thay thế. `contentHash` response là null ở Draft/Rejected hoặc khi snapshot chưa đầy đủ; DB vẫn giữ hash lịch sử tới khi sửa/submit lại.

## Quyền và khóa

Admin xem toàn bộ. Head xem đề cùng tổ hoặc đề của mình. Teacher xem đề của mình và metadata phiên bản Published cùng tổ; không được lấy endpoint chi tiết chứa đáp án của người khác (403), và không thấy các phiên bản riêng tư qua list/detail (404). B10 sẽ cung cấp student-sheet riêng. LatestVersionNo trong metadata là số mới nhất người gọi được thấy, không lộ số phiên bản riêng tư.

Với Draft/Rejected, kiểm `QuestionRules.CanRead` trên từng nguồn trước khi dựng item: nguồn không được xem giữ id/thứ tự/điểm/sourceRevision, còn stem/payload/explanation/type/difficulty/currentSourceRevision là null và trả warning `SourceContentUnavailable`. Quyền xem đề không tự cấp quyền xem câu Draft/Rejected riêng của giáo viên. Archive Rejected không được dùng làm đường vòng chụp nội dung riêng tư. Các summary đếm bằng SQL `Questions.Count()` và không tải snapshot/đáp án chỉ để đếm. User có role/tổ không hợp lệ trả 401 và Bearer challenge như A3.

Tác giả chuyển tổ vẫn xem được câu Approved đủ điều kiện chọn trong chính đề cũ theo tổ cố định của đề, để tiếp tục soạn/submit. Ngoại lệ này không cấp quyền xem Draft/Rejected của người khác hoặc GET trực tiếp câu ngoài tổ.

Mọi mutation giữ `FOR SHARE` trên hàng actor và catalog shared advisory lock `48683402`, sau đó `FOR UPDATE` trên Exam rồi ExamVersion. Nguồn lấy `FOR SHARE` theo id trước khi kiểm và ghi. Khóa Exam serialize cấp versionNo và publish. Partial unique index A2 vẫn là lớp bảo vệ cuối cho một bản Published.

A6 khóa nguồn trước phiên bản để bảo vệ sửa/archive nguồn; A7 khóa phiên bản trước nguồn để bảo vệ snapshot. PostgreSQL có thể phát hiện deadlock giữa hai đường: transaction rollback và API trả 409; không tự retry một mutation. `lock_timeout` là 2 giây. Endpoint ghi kiểm visibility trước khi chờ hàng, rồi đọc lại và kiểm sau khóa. Actor mất/bị khóa trả 401 kèm `WWW-Authenticate: Bearer`.

Trigger A2 không cho sửa item của bản frozen. Submit lưu snapshot khi còn Draft/Rejected trước, rồi chuyển trạng thái; withdraw mở Draft trước rồi mới xóa snapshot/hash. Mọi bước và audit trong cùng transaction. Audit lỗi thì rollback cả nội dung và trạng thái. Không ghi email/nội dung câu vào note.

## Hàm chính

| Hàm | Tác dụng |
| --- | --- |
| `ExamRules.RequireAction` | Kiểm chủ sở hữu, tổ, role, tự duyệt và trạng thái nguồn trước quyết định |
| `ExamRules.Transition` | Áp dụng trạng thái, token lượt gửi, reviewer/publisher và timestamp |
| `ValidateComposition` | Kiểm thời gian, số câu, id trùng, điểm và tổng decimal |
| `ContentHash` / `WriteCanonical` | Tạo hash nội dung ổn định với thứ tự key JSON và scale điểm |
| `MutateAsync` | Bọc khóa actor/catalog, transaction và ánh xạ lỗi PostgreSQL an toàn |
| `LockExamAsync` / `LockVersionAsync` | Kiểm visibility trước khóa và kiểm lại dưới khóa |
| `LockSourcesAsync` | Giữ nguồn ổn định từ kiểm quyền/trạng thái tới commit |
| `SnapshotAsync` | Kiểm mọi nguồn, sao chép nội dung và revision, tính tổng/hash |
| `CreateVersionAsync` | Cấp versionNo kế tiếp, clone liên kết hiện tại và trả warning nguồn bị bỏ |
| `VersionViewAsync` | Trả nguồn hiện tại cho editable, snapshot cho frozen và chặn lộ đáp án |
| `ReviewQueueAsync` | Phân trang Submitted theo giờ gửi, chỉ reviewer đúng tổ/admin, loại đề tự tạo |

Các hàm nghiệp vụ/service/controller có XML comment tiếng Anh; tài liệu giải thích tiếng Việt ở trên.

## Kiểm thử

```powershell
dotnet test backend/ExamMaker.slnx -c Release -m:1
```

Đặt `A2_TEST_PG_ADMIN` như hướng dẫn A2/A6 để chạy PostgreSQL trong database test tạm. Không dùng dữ liệu thật; fixture tạo rồi xóa database ngẫu nhiên, không sửa database dev.

Kiểm tra local ngày 21/11/2026: **600/600 test backend Release pass, 0 failed, 0 skipped** (411 API/quy tắc, 38 Application, 151 Infrastructure). A7 bổ sung 91 test, trong đó 15 test PostgreSQL thật. Frontend: TypeScript typecheck và **148/148 test trong 11 file pass** sau khi nạp dependency B2 từ lockfile; không đổi package.json/package-lock.json. Formatter whitespace và kiểm đồng bộ schema frontend đạt.

Test gồm 42 cặp trạng thái/hành động; ma trận reviewer/visibility/answer access; canonical hash; token JavaScript và clock lùi; HTTP JWT/routing/ProblemDetails; full workflow qua PostgreSQL, clone, nguồn chưa duyệt, cạnh tranh publish/versionNo và rollback snapshot/audit. Test chờ khóa có poll `pg_blocking_pids` trước khi commit archive đồng thời; phối hợp A6/A7 bảo vệ nguồn Draft/Submitted và giữ snapshot sau khi đề Approved.

## Trước merge

- Chạy toàn backend với PostgreSQL, format và kiểm schema frontend đồng bộ.
- Tạo PR từ `feat/a7-exam-workflow` vào `dev`, kiểm CI/review; không tick A7 hoàn tất trước merge.
- B9 dùng các endpoint đã triển khai để làm UI soạn đề. B10 phải đọc snapshot và tách payload học sinh khỏi đáp án, không dùng nguyên response `ExamVersion` cho học sinh.
