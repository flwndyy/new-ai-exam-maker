# Hướng dẫn đóng góp

Đọc `docs/context.md` trước khi bắt đầu.

## Quy trình

1. Tạo nhánh ngắn từ `main`: `feat/<mô-tả>`, `fix/<mô-tả>`, `infra/<mô-tả>`. Không có nhánh `develop`.
2. Commit nhỏ, rõ nghĩa; **commit message và tên nhánh viết bằng tiếng Anh** (vd `feat: add question approval endpoint`). Không push thẳng vào `main`.
3. Mở Pull Request, điền template. Cần 1 approval (và review của Code Owner), CI pass, resolve hết conversation.
4. Chỉ dùng **Squash merge**. Nhánh tự xoá sau khi merge.

## Quy tắc bảo mật (bất biến)

- Không commit: key, connection string, `.env`, `.pem`, account ID/ARN thật, Elastic IP, dữ liệu đề thi/giáo viên thật, tài liệu có bản quyền (SGK...).
- Chỉ commit `.env.example` với giá trị giả.
- Nếu lỡ lộ secret: **thu hồi/rotate key ngay**, sau đó mới dọn lịch sử. Xoá commit không đủ.
- Bật 2FA cho tài khoản GitHub.

## Pre-commit

Cài [pre-commit](https://pre-commit.com) rồi chạy một lần:

```sh
pre-commit install
```

Hook gitleaks sẽ chặn commit chứa secret.

## Phân quyền thư mục

| Thư mục | Người review |
| --- | --- |
| `/frontend`, `/backend` | 2 người code |
| `/infra`, `/.github` | 2 người AWS |
