# Hướng dẫn đóng góp

Đọc `docs/context.md` trước khi bắt đầu.

## Quy trình

Mô hình nhánh: `main` (ổn định, được bảo vệ) ← `dev` (nhánh tích hợp, sống lâu) ← nhánh ngắn.

1. Tạo nhánh ngắn **từ `dev`**: `feat/<mô-tả>`, `fix/<mô-tả>`, `infra/<mô-tả>`.
2. Commit nhỏ, rõ nghĩa; **commit message và tên nhánh viết bằng tiếng Anh** (vd `feat: add question approval endpoint`). Không push thẳng vào `main`.
3. Mở Pull Request **vào `dev`**, điền template, merge bằng **Squash**. Nhánh ngắn tự xoá sau khi merge.
4. Khi `dev` ổn định, mở PR **`dev` → `main`**: cần 1 approval (và review của Code Owner), CI pass, resolve hết conversation. PR này **chỉ** được merge bằng **Merge commit** (không Squash) để `dev` và `main` không bị lệch nhau.
5. Không xoá, không force push `dev` và `main` (đã bị chặn bằng ruleset).
6. Trước khi tạo nhánh mới, `git pull` `dev`; sau khi `dev` → `main` được merge, `git pull origin main` vào `dev` nếu `dev` báo lệch.

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
