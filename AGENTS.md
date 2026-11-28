# AGENTS.md

Tài liệu ngữ cảnh gốc của dự án: @docs/context.md — đọc trước khi viết code.

## Cấu trúc repo

- `/frontend` — giao diện web (Next.js, build static, đẩy lên S3 + CloudFront)
- `/backend` — API + worker (.NET, chạy trên EC2)
- `/infra` — hạ tầng AWS
- `/docs` — tài liệu (context.md, ERD, API contract)

## Quy tắc bất biến

- Không commit key, connection string, `.env`, `.pem`, account ID/ARN thật, Elastic IP, dữ liệu đề thi/giáo viên thật, tài liệu có bản quyền (SGK...).
- Lộ secret → phải thu hồi key, không chỉ xoá commit.
- Không push thẳng vào `main`; làm trên nhánh `feat/...`, `fix/...`, `infra/...` tách từ `dev`, mở PR vào `dev` (Squash). Từ `dev` lên `main` bằng PR **Merge commit** (xem `CONTRIBUTING.md`).
- Không tự commit/push khi chưa được yêu cầu.
- Code, tên biến/bảng, commit message, tên nhánh: tiếng Anh. Tài liệu trong `docs/` và giao diện: tiếng Việt.
- Đổi quy tắc nghiệp vụ/kiến trúc → sửa `docs/context.md` trước rồi mới code.
- Không bắt đầu viết code tính năng khi các mục C trong checklist (state machine duyệt đề, CloudFront→EC2, phiên bản đề, idempotency SQS, ngân sách, dạng câu hỏi MVP, ERD + API contract) chưa chốt.
