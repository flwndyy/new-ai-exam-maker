# Khung hạ tầng AWS

Thư mục này chứa các file mẫu triển khai theo AWS Console cho demo AI Exam Maker. Đây là
khung cấu hình, chưa phải IaC hoàn chỉnh. Xem [`../docs/aws-runbook.md`](../docs/aws-runbook.md)
để biết thứ tự triển khai.

## Phạm vi

- `bootstrap/`: mẫu user-data cho EC2 ARM64.
- `nginx/`: mẫu reverse proxy cho `origin.<domain>`, gồm HTTP-01 và TLS.
- `systemd/`: mẫu service cho API, Worker và timer gia hạn Certbot.
- `certbot/`: deploy hook kiểm tra và reload Nginx sau khi chứng chỉ gia hạn thành công.
- `cloudfront-function/`: function viewer-request cho route static export.
- `iam/`: policy mẫu cho instance profile EC2; thay placeholder trong AWS, không commit ARN thật.
- `cognito/`: script dựng và xác minh Cognito User Pool, nhóm người dùng và App Client (Task O9 — xem [`../docs/runbooks/cognito.md`](../docs/runbooks/cognito.md)).
- `rds/`: script dựng, xác minh 15 tiêu chí và kịch bản Restore Drill cho Amazon RDS PostgreSQL 16 (Task O10 — xem [`../docs/runbooks/rds.md`](../docs/runbooks/rds.md)).
- `sqs/`: script dựng và xác minh Amazon SQS + DLQ cho pipeline AI (Task O11 — xem [`../docs/runbooks/sqs.md`](../docs/runbooks/sqs.md)).
- `ssm/`: script chuẩn hóa và xác minh cây tham số AWS Systems Manager Parameter Store (Task O12 — xem [`../docs/runbooks/parameter-store.md`](../docs/runbooks/parameter-store.md)).

## Quy tắc placeholder

Các file có đuôi `.example` chỉ chứa placeholder. Thay thế trong lúc triển khai hoặc lấy giá
trị từ AWS Systems Manager Parameter Store. Không commit secret, account ID, resource ID,
ARN, private key hoặc domain production thật vào thư mục này.

VPC, subnet, Security Group, EC2, S3, CloudFront và DNS thật được tạo theo hướng dẫn ở O2–O5.
Cognito (O9) và RDS PostgreSQL (O10) được triển khai và kiểm tra tự động thông qua các script
trong `infra/cognito/` và `infra/rds/` kết hợp lưu trữ tham số an toàn trong SSM Parameter Store.
O1 không tự provision tài nguyên AWS.
