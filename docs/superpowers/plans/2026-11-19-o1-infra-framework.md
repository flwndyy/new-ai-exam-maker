# Kế hoạch triển khai khung hạ tầng O1

> **Dành cho agent:** dùng `superpowers:executing-plans` để thực hiện kế hoạch theo từng task.

**Mục tiêu:** Tạo khung triển khai AWS theo Console và runbook O2–O5, không tạo tài nguyên AWS và không lưu secret/ID/ARN thật.

**Kiến trúc:** File hạ tầng nằm trong `/infra`, hướng dẫn vận hành nằm trong `docs/`. Khung gồm bootstrap cho EC2, Nginx, systemd và CloudFront Function; O2–O5 sẽ tạo tài nguyên thật trên Console.

**Công nghệ:** shell script, Nginx, systemd, CloudFront Function JavaScript, Markdown.

**Tài liệu nguồn:** `docs/context.md` mục 5.2–5.4, 11, 14; `docs/tasks.md` O1 và định nghĩa hoàn thành.

## Ràng buộc chung

- Dùng AWS Console và file/script mẫu trong `/infra`; IaC hoàn chỉnh để sau MVP.
- EC2 dùng `t4g.small`/ARM64; backend publish `linux-arm64` self-contained.
- Secret/config runtime lấy từ Parameter Store; không commit access key, secret, ID hoặc ARN thật.
- Không tạo NAT Gateway, ALB, SSH hoặc Multi-AZ trong phạm vi O1.
- Origin dùng `origin.<domain>` và Let's Encrypt HTTP-01; port 80 chỉ cho ACME, port 443 chỉ nhận CloudFront.
- Việc hạ tầng phải có hướng dẫn dựng và kiểm tra, không chỉ thao tác tay trên Console.

## Phạm vi thực hiện

- Tạo scaffold cho bootstrap EC2, Nginx HTTP/TLS, systemd và CloudFront Function.
- Dùng symlink `/opt/exammaker/current` trỏ tới một release trong `/opt/exammaker/releases/`.
- Giữ origin secret trong file include riêng, owner `root:nginx`, mode `0640`.
- Giới hạn bộ nhớ API/Worker phù hợp với EC2 2 GB RAM.
- Viết runbook tiếng Việt có thứ tự O2–O5 và checklist xác minh.
