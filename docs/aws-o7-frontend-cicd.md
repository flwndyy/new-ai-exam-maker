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
| `NEXT_PUBLIC_COGNITO_DOMAIN` | Cognito domain dùng cho frontend |
| `NEXT_PUBLIC_COGNITO_CLIENT_ID` | Cognito app client ID |

Không commit các giá trị trên vào repository. `AWS_ROLE_ARN` và các biến
`NEXT_PUBLIC_*` không phải access key; workflow vẫn xác thực AWS bằng OIDC.

## Cách chạy

- Push vào `main` khi có thay đổi trong `frontend/`, OpenAPI, CloudFront Function
  hoặc chính workflow sẽ tự deploy.
- Có thể chạy thủ công bằng **Actions → Deploy frontend → Run workflow**, chọn
  branch `main`.
- Workflow chỉ cấp `contents: read` và `id-token: write`, build bằng Node.js 22,
  upload artifact `frontend-out-<commit-sha>` trong 14 ngày để tải xuống khi cần
  rollback.

## Cache và rollback

HTML/non-fingerprinted assets dùng cache tối đa 60 giây; `_next/static/*` và
`assets/*` dùng cache immutable 1 năm. Sau mỗi lần upload workflow tạo invalidation
`/*`.

Để rollback: tải artifact `frontend-out-<commit-sha>` từ workflow run cần khôi
phục, giải nén thành `frontend/out/`, rồi chạy lại hai lệnh `aws s3 sync` và
`aws s3 cp` trong `docs/aws-o5-s3-cloudfront.md`, tiếp theo tạo invalidation.
