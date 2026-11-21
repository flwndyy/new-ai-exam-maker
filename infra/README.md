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
- `sqs/`: script dựng và xác minh Amazon SQS + DLQ cho pipeline AI (Task O11 — xem [`../docs/runbooks/sqs.md`](../docs/runbooks/sqs.md)).

## Quy tắc placeholder

Các file có đuôi `.example` chỉ chứa placeholder. Thay thế trong lúc triển khai hoặc lấy giá
trị từ AWS Systems Manager Parameter Store. Không commit secret, account ID, resource ID,
ARN, private key hoặc domain production thật vào thư mục này.

VPC, subnet, Security Group, EC2, RDS, S3, CloudFront và DNS thật được tạo trên AWS Console
ở O2–O5. O1 không tự provision tài nguyên AWS.
