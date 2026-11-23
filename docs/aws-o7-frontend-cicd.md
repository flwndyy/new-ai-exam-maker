# O7 — CI/CD frontend

Workflow `.github/workflows/deploy-frontend.yml` build frontend static export,
đồng bộ lên S3 rồi invalidate CloudFront bằng role OIDC của O6.

## Điều kiện trước khi chạy

- O5 đã có S3 bucket private và CloudFront distribution.
- O6 đã có role `aem-github-frontend-deploy-role`.
- Role đã được gắn policy mẫu `infra/iam/github-actions-frontend-deploy-policy.example.json`
  với ARN thật của bucket và distribution.
- Trust policy O6 vẫn khớp `workflow: Deploy frontend`, `ref: refs/heads/main` và
  `sub` của repository. Không đặt `environment:` trong job này.

## Repository Variables

Tạo các **Repository Variables** trong GitHub (Settings → Secrets and variables →
Actions → Variables), không tạo GitHub Environment:

| Tên | Giá trị |
|---|---|
| `AWS_ROLE_ARN` | ARN role O6, dạng `arn:aws:iam::<account-id>:role/aem-github-frontend-deploy-role` |
| `FRONTEND_BUCKET_NAME` | Tên bucket S3 frontend, không có `s3://` |
| `CLOUDFRONT_DISTRIBUTION_ID` | Distribution ID, không phải ARN |
| `NEXT_PUBLIC_COGNITO_DOMAIN` | Cognito domain dạng `https://<prefix>.auth.<region>.amazoncognito.com` |
| `NEXT_PUBLIC_COGNITO_CLIENT_ID` | Cognito app client ID |

Không commit các giá trị trên vào repository. `AWS_ROLE_ARN` và các biến
`NEXT_PUBLIC_*` không phải access key; workflow vẫn xác thực AWS bằng OIDC.

## Cách chạy

- Push vào `main` khi có thay đổi trong `frontend/`, OpenAPI hoặc chính workflow
  sẽ tự deploy. Thay đổi CloudFront Function không được workflow này publish.
- Có thể chạy thủ công bằng **Actions → Deploy frontend → Run workflow**, chọn
  branch `main`. Job deploy chỉ chạy khi ref là `main`.
- Workflow chỉ cấp `contents: read` và `id-token: write`, build bằng Node.js 22,
  upload artifact `frontend-out-<commit-sha>` trong 90 ngày để tải xuống khi cần
  rollback. Rerun cùng một workflow run ghi đè artifact cùng tên.

## Cache và rollback

HTML/non-fingerprinted assets dùng cache tối đa 60 giây; `_next/static/*` và
`assets/*` dùng cache immutable 1 năm. Sau mỗi lần upload workflow tạo invalidation
`/*`.

Các file fingerprinted trong `_next/static/*` và `assets/*` được giữ lại có chủ
đích: HTML cũ còn cache hoặc artifact rollback vẫn có thể tham chiếu đến chúng.
Nếu cần kiểm soát dung lượng bucket, hãy thêm S3 Lifecycle Rule riêng sau khi
chốt thời gian rollback; không dùng `--delete` cho hai prefix này trong workflow.

Để rollback: tải artifact `frontend-out-<commit-sha>` từ workflow run cần khôi
phục, giải nén thành `frontend/out/`, rồi chạy lại hai lệnh `aws s3 sync` và
`aws s3 cp` trong `docs/aws-o5-s3-cloudfront.md`, tiếp theo tạo invalidation.
