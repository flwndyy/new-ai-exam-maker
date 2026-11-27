# Trạng thái phiên bản trong danh sách đề: kế hoạch triển khai (PR 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Mục tiêu:** Schema `Exam` (dùng cho `GET /exams`, `GET /exams/{id}` và response tạo/sửa đề) trả thêm `latestVersionId`, `latestVersionStatus` và `publishedVersionNo`. Các trường này tính trên đúng tập phiên bản người xem đọc được.

**Kiến trúc:** Chỉ thêm trường đọc, không thêm migration, không đổi quyền. Cả ba trường được tính trong hàm `ExamView(Exam, AuthenticatedUser)` của `ExamManagement.cs`, trên tập `visible` đang dùng để tính `latestVersionNo` và `publishedVersionId`. Đi theo thứ tự hợp đồng trước, code sau: `context.md` và `openapi.yaml`, rồi `schema.d.ts` của frontend, cuối cùng là .NET.

**Tech stack:** .NET 10, EF Core + Npgsql, xUnit (`[PostgresFact]` cần PostgreSQL thật), openapi-typescript.

**Spec:** [`docs/superpowers/specs/2026-11-25-frontend-redesign-design.md`](../specs/2026-11-25-frontend-redesign-design.md), mục 6.11 và 10.

## Ràng buộc chung

- Nhánh `feat/exam-list-version-status` tách từ `dev`, PR vào `dev` (Squash).
- **Không tự `git commit`/`git push`/tạo PR.** Mỗi task kết thúc bằng một bước "Điểm dừng": để thay đổi trong working tree, báo người dùng tự commit. Cuối plan, viết mô tả PR dạng Markdown để người dùng dán vào.
- Sửa `docs/context.md` và `docs/api/openapi.yaml` **trước** code. Tài liệu viết tiếng Việt; code, tên và comment kỹ thuật viết tiếng Anh.
- Chỉ **thêm** trường, không đổi hay xóa trường cũ (tương thích ngược). Không có migration DB.
- Quy tắc quyền: tập phiên bản dùng tính trường mới là `e.Versions.Where(v => ExamRules.CanRead(actor, e, v.Status))`, giống hệt `latestVersionNo` hiện tại.
- Phiên bản `context.md` của PR này là **0.25**. PR frontend sau đó dùng 0.26.

## Trọng tâm review

1. Giáo viên xem đề của đồng nghiệp cùng tổ khi tác giả đã có v2 `Draft` hoặc `Rejected` sau v1 `Published`: phải thấy `latestVersionStatus = Published` và `latestVersionId = v1`, không lộ v2 (test ở Task 2).
2. Đề chưa từng xuất bản: `publishedVersionNo` là `null`, không phải 0 (Task 2).
3. Thu hồi xuất bản (`Unpublish`): `publishedVersionNo` trở về `null`, `latestVersionStatus` phản ánh `Approved` (Task 2).
4. Response sau khi tạo phiên bản mới ở frontend (`version-actions.tsx` cập nhật cache `["exam", id]`) phải cập nhật cả `latestVersionId` và `latestVersionStatus`, nếu không danh sách sẽ hiện trạng thái cũ (Task 1).
5. `schema.d.ts` lệch với `openapi.yaml` làm CI `api:check` hỏng (Task 1).

---

### Task 1: Hợp đồng API và kiểu frontend

**Files:**
- Modify: `docs/context.md`, phần "Chi tiết triển khai A7" (mục 4.3) và "Lịch sử thay đổi" (thêm **0.25 — 25/11/2026** phía trên 0.24)
- Modify: `docs/api/openapi.yaml`, schema `Exam` (khoảng dòng 2081)
- Modify: `frontend/src/lib/api/schema.d.ts` (sinh lại, không sửa tay)
- Modify: `frontend/src/components/exams/version-actions.tsx:72` (cập nhật cache)
- Modify: `frontend/src/app/exams/page.test.tsx:48`, `frontend/src/app/exams/view/page.test.tsx:97` (fixture)
- Test: `frontend/src/components/exams/version-actions.test.tsx`

**Interfaces:**
- Produces: các trường của schema `Exam`: `latestVersionId: string (uuid)` bắt buộc, `latestVersionStatus: ExamVersionStatus` bắt buộc, `publishedVersionNo: integer | null` (có trong `required`, cho phép null). Kiểu TS tương ứng là `components["schemas"]["Exam"]`.

- [ ] **Step 1: Sửa `docs/context.md`**

Trong "Chi tiết triển khai A7", thêm một gạch đầu dòng: danh sách và chi tiết đề trả thêm `latestVersionId`, `latestVersionStatus` (phiên bản có `versionNo` lớn nhất trong số phiên bản người xem đọc được) và `publishedVersionNo` (số của phiên bản `Published` người xem đọc được, `null` nếu không có). Các trường này tính trên cùng tập phiên bản với `latestVersionNo` nên không lộ phiên bản riêng tư. Thêm mục lịch sử 0.25 ghi thay đổi này và lý do (màn danh sách đề và trang chủ của đợt làm mới giao diện). Đổi dòng phiên bản ở đầu file thành "Phiên bản 0.25".

- [ ] **Step 2: Sửa `docs/api/openapi.yaml`**

Trong schema `Exam`, thêm `latestVersionId`, `latestVersionStatus` và `publishedVersionNo` vào `required`, kèm khai báo:
```yaml
        latestVersionId:
          type: string
          format: uuid
          description: Phiên bản có `versionNo` lớn nhất trong số phiên bản người xem đọc được.
        latestVersionStatus: { $ref: '#/components/schemas/ExamVersionStatus' }
        publishedVersionNo:
          type: [integer, 'null']
          minimum: 1
          description: Số của phiên bản `Published` người xem đọc được; `null` nếu không có.
```

- [ ] **Step 3: Sinh lại kiểu và kiểm tra đồng bộ**

Run: `cd frontend && npm run api:generate && npm run api:check`
Expected: `API types are in sync with docs/api/openapi.yaml.`

- [ ] **Step 4: Viết test thất bại cho cập nhật cache khi tạo phiên bản mới**

Trong `version-actions.test.tsx`, thêm test `"creating a version updates latest version id and status in the exam cache"`. Seed cache `["exam", examId]` với `latestVersionNo: 1, latestVersionId: "V1", latestVersionStatus: "Published"`. Mock `POST /exams/{id}/versions` trả `{ id: "V2", versionNo: 2, status: "Draft", ... }`. Sau khi bấm "Tạo phiên bản mới", assert:
```ts
expect(cached.latestVersionNo).toBe(2);
expect(cached.latestVersionId).toBe("V2");
expect(cached.latestVersionStatus).toBe("Draft");
```

- [ ] **Step 5: Chạy test, xác nhận thất bại**

Run: `cd frontend && npx vitest run src/components/exams/version-actions.test.tsx`
Expected: FAIL ở `latestVersionId`.

- [ ] **Step 6: Sửa cập nhật cache trong `version-actions.tsx`**

Ở chỗ đang đặt `latestVersionNo: Math.max(old.latestVersionNo, data.versionNo)`: khi `data.versionNo >= old.latestVersionNo` thì đặt thêm `latestVersionId: data.id` và `latestVersionStatus: data.status`, ngược lại giữ giá trị cũ.

- [ ] **Step 7: Bổ sung fixture**

Thêm `latestVersionId`, `latestVersionStatus` và `publishedVersionNo` vào các object `Exam` trong `exams/page.test.tsx` (`"V2"`, `"Draft"`, `1`, khớp `publishedVersionId: "V1"`) và `exams/view/page.test.tsx` (`"V2"`, `"Draft"`, `null`).

- [ ] **Step 8: Chạy toàn bộ kiểm tra frontend**

Run: `cd frontend && npm run typecheck && npm test && npm run lint`
Expected: không lỗi, mọi test PASS.

- [ ] **Step 9: Điểm dừng.** Báo người dùng commit, gợi ý message `docs(api): add latest version status to exam schema`.

---

### Task 2: Tính ba trường mới ở backend

**Files:**
- Modify: `backend/src/ExamMaker.Application/Exams/ExamContracts.cs:16-17` (record `ExamView`)
- Modify: `backend/src/ExamMaker.Infrastructure/Exams/ExamManagement.cs:369-374` (hàm `ExamView`)
- Test: `backend/tests/ExamMaker.Infrastructure.Tests/ExamManagementTests.cs`

**Interfaces:**
- Consumes: hợp đồng ở Task 1.
- Produces: `public record ExamView(Guid Id, string Title, Guid SubjectId, int Grade, Guid DepartmentId, QuestionUser CreatedBy, int LatestVersionNo, Guid? PublishedVersionId, Guid LatestVersionId, string LatestVersionStatus, int? PublishedVersionNo, DateTime CreatedAt, DateTime UpdatedAt)`. Serializer hiện tại tự xuất camelCase nên JSON có `latestVersionId`, `latestVersionStatus` và `publishedVersionNo`.

- [ ] **Step 1: Viết test thất bại**

Thêm `[PostgresFact] List_and_detail_report_latest_visible_version_and_published_number` vào `ExamManagementTests`, dùng các helper có sẵn (`SeedAsync`, `SourceAsync`, `CreateAsync`, `ComposeAsync`, `ActAsync`, `Service`, cùng các actor `owner`, `peer`, `head`, `admin`). Kịch bản và assert:
```csharp
// v1 Draft
var owned = Assert.Single((await Service(db).ListAsync(owner, new(), default)).Items);
Assert.Equal(v1, owned.LatestVersionId); Assert.Equal("Draft", owned.LatestVersionStatus); Assert.Null(owned.PublishedVersionNo);
// v1 Submit → Approve → Publish, then create v2, compose, Submit, head Reject
// owner and head see v2 Rejected with PublishedVersionNo 1
Assert.Equal((v2, "Rejected", 1), (o.LatestVersionId, o.LatestVersionStatus, o.PublishedVersionNo));
Assert.Equal((v2, "Rejected", 1), (h.LatestVersionId, h.LatestVersionStatus, h.PublishedVersionNo));
// peer teacher in the same department only sees the published v1
Assert.Equal((v1, "Published", 1, 1), (p.LatestVersionId, p.LatestVersionStatus, p.PublishedVersionNo, p.LatestVersionNo));
// GetAsync returns the same values as ListAsync for each actor
// admin Unpublish v1 → owner sees PublishedVersionNo null; peer no longer sees the exam in the list
```

- [ ] **Step 2: Chạy test, xác nhận thất bại**

Run: `cd backend && dotnet test tests/ExamMaker.Infrastructure.Tests --filter "FullyQualifiedName~List_and_detail_report_latest_visible_version"` (cần đặt biến `A2_TEST_PG_ADMIN` trỏ tới PostgreSQL tạm có quyền CREATEDB)
Expected: lỗi biên dịch vì `ExamView` chưa có `LatestVersionId`.

- [ ] **Step 3: Thêm ba tham số vào record `ExamView`** theo đúng thứ tự ở khối Interfaces.

- [ ] **Step 4: Tính giá trị trong hàm `ExamView(Exam e, AuthenticatedUser actor)`**

Trên mảng `visible` có sẵn: `latest = visible.MaxBy(v => v.VersionNo)!`, `published = visible.SingleOrDefault(v => v.Status == "Published")`. Truyền `latest.VersionNo`, `published?.Id`, `latest.Id`, `latest.Status`, `published?.VersionNo`. Tìm và sửa mọi chỗ khác gọi constructor `ExamView` (hiện chỉ có hàm này).

- [ ] **Step 5: Chạy test, xác nhận thành công**

Run: lệnh ở Step 2
Expected: PASS.

- [ ] **Step 6: Chạy toàn bộ test backend**

Run: `cd backend && dotnet build && dotnet test`
Expected: không lỗi. Các test `ExamsHttpTests`, `A9PermissionMatrixTests` và `ExamManagementTests` cũ vẫn PASS.

- [ ] **Step 7: Điểm dừng.** Báo người dùng commit, gợi ý message `feat(api): return latest visible version status in exam list`.

---

### Task 3: Mô tả PR

- [ ] **Step 1:** Viết mô tả PR tiếng Việt (tiêu đề tiếng Anh: `feat(api): expose latest version status and published number on exams`) vào thư mục scratchpad. Nội dung gồm: lý do (spec mục 6.11), ba trường mới, quy tắc quyền, phạm vi test, lưu ý "không migration, tương thích ngược", và dòng `🤖 Generated with [Claude Code](https://claude.com/claude-code)` ở cuối. Báo đường dẫn cho người dùng.
