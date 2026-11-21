# Kế hoạch triển khai O5 S3 và CloudFront

> Tài nguyên AWS thật được tạo bằng Console theo runbook. Repository chỉ chứa cấu hình mẫu,
> CloudFront Function và kiểm tra; không commit domain, IP, ARN hoặc secret thật.

## Mục tiêu

Hoàn thiện O5 cho frontend static export và đường dẫn API:

- S3 private chỉ được đọc qua CloudFront Origin Access Control (OAC).
- Behavior mặc định `/*` phục vụ S3, cache bình thường và rewrite route static export bằng
  CloudFront Function.
- Behavior `/api/*` đi tới `origin.<domain>` qua HTTPS, không cache, nhận đủ method/header
  cần thiết và gửi `X-Origin-Verify` cùng `CloudFront-Viewer-Address`.
- Có runbook kiểm tra cả security boundary lẫn deep-link routing.

## Nhiệm vụ 1: Kiểm tra và bảo vệ CloudFront Function

**Tệp:**

- Sửa nếu cần: `infra/cloudfront-function/index-html-rewrite.js`
- Tạo: `infra/cloudfront-function/index-html-rewrite.test.mjs`

**Cách làm:**

1. Giữ nguyên `/api/*` để API không bị rewrite.
2. Rewrite `/` và route không có phần mở rộng tới `index.html` tương ứng.
3. Giữ nguyên asset có phần mở rộng.
4. Dùng Node test runner không cần thêm dependency.

## Nhiệm vụ 2: Tài liệu triển khai O5

**Tệp:**

- Tạo: `docs/aws-o5-s3-cloudfront.md`
- Sửa: `docs/aws-runbook.md`

**Nội dung:**

1. Điều kiện trước khi làm và quy ước placeholder.
2. Tạo S3 bucket private, Block Public Access, Bucket owner enforced, không dùng website
   endpoint.
3. Tạo OAC với SigV4 và bucket policy giới hạn `cloudfront.amazonaws.com` theo distribution.
4. Tạo CloudFront distribution, ACM `us-east-1`, alternate domain `app.<domain>`.
5. Cấu hình `/api/*`: origin HTTPS, redirect viewer HTTP, đủ method, `CachingDisabled`,
   origin request policy không forward Host và có `CloudFront-Viewer-Address`, custom
   `X-Origin-Verify` lấy từ Parameter Store.
6. Cấu hình `/*`: S3 REST origin, cache policy phù hợp, Function viewer-request; không dùng
   custom error 403/404 để làm SPA fallback.
7. DNS, upload static export, invalidation và checklist kiểm tra.

## Nhiệm vụ 3: Xác minh

- Chạy test CloudFront Function.
- Chạy `git diff --check`.
- Chạy frontend lint/typecheck/build và backend test theo CI nếu môi trường cho phép.
- Quét diff để bảo đảm không có secret, IP, ARN hoặc domain production thật.
