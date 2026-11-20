# Kế hoạch triển khai O4 Domain, DNS và HTTPS

> **Dành cho agent:** dùng executing-plans để triển khai kế hoạch theo từng nhiệm vụ.

**Mục tiêu:** Hoàn thiện cấu hình ứng dụng và runbook cho domain, DNS, ACM, Nginx HTTPS,
Let's Encrypt, timer gia hạn, origin header guard và forwarded headers.

**Kiến trúc:** Tài nguyên thật được tạo trên AWS Console và EC2 qua SSM. CloudFront dùng
ACM ở `us-east-1`; origin EC2 dùng Let's Encrypt cho `origin.<domain>`. Repository chỉ có
placeholder, không chứa domain/IP/ARN/secret thật.

**Đặc tả:** O4 trong `docs/tasks.md`, `docs/context.md` mục 5.3 và `docs/aws-runbook.md`.

## Ràng buộc chung

- EC2/RDS/runtime AWS ở `ap-southeast-1`; ACM CloudFront ở `us-east-1`.
- Không mở SSH; port 80 chỉ phục vụ HTTP-01; port 443 chỉ nhận CloudFront.
- Origin secret lấy từ Parameter Store, file Nginx secret owner `root:nginx`, mode `0640`.
- Không commit domain thật, IP, ARN, private key hoặc secret.

## Nhiệm vụ 1: Forwarded headers

**Tệp:**
- Sửa: `backend/src/ExamMaker.Api/Program.cs`

- [ ] Tin `X-Forwarded-For` và `X-Forwarded-Proto` từ proxy loopback.
- [ ] Đặt middleware trước request logging, authentication và authorization.

## Nhiệm vụ 2: Runbook O4

**Tệp:**
- Tạo: `docs/aws-o4-domain-https.md`
- Sửa: `docs/aws-runbook.md`

- [ ] Ghi DNS `origin`/`app`, ACM `us-east-1`, HTTP-only ACME, Let's Encrypt và Nginx HTTPS.
- [ ] Ghi timer/deploy hook và cách kiểm tra origin header/forwarded headers.

## Nhiệm vụ 3: Kiểm tra

- [ ] Chạy `dotnet test` cho solution backend.
- [ ] Chạy `git diff --check`.
- [ ] Parse các file JSON thay đổi nếu có.
- [ ] Quét diff không có secret/domain/IP/ARN thật.
