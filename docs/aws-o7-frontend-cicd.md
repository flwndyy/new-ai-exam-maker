# O7 — Frontend CI/CD qua S3 + SSM + EC2

## Mục tiêu

Mỗi lần push vào `main`, GitHub Actions build frontend tĩnh, lưu một release có mã commit vào S3, rồi gọi AWS Systems Manager để EC2 tải và triển khai release đó cho Nginx.

Luồng này không phụ thuộc CloudFront, không cần SSH và không lưu AWS access key dài hạn trong GitHub.

```text
GitHub Actions -> build frontend/out -> tar.gz + SHA-256
  -> S3 frontend/releases/<commit>-<attempt>/
  -> SSM AWS-RunShellScript -> EC2 tải/kiểm checksum/giải nén
  -> cập nhật symlink current -> test/reload Nginx -> kiểm tra asset
```

## File triển khai

- Workflow: `.github/workflows/deploy-frontend.yml`
- Script chạy trên EC2: `infra/deploy/deploy-frontend-on-ec2.sh`
- Mẫu policy GitHub role: `infra/iam/github-actions-frontend-deploy-policy.example.json`
- Mẫu trust policy OIDC: `infra/iam/github-actions-oidc-trust-policy.example.json`

## GitHub Repository Variables

Tạo tại **Settings → Secrets and variables → Actions → Variables → New repository variable**:

| Tên | Giá trị |
|---|---|
| `AWS_ROLE_ARN` | ARN của `aem-github-frontend-deploy-role` |
| `FRONTEND_BUCKET_NAME` | Tên bucket S3 frontend |
| `EC2_INSTANCE_ID` | Instance ID EC2 đích |
| `FRONTEND_PUBLIC_URL` | URL HTTPS public của app |
| `NEXT_PUBLIC_COGNITO_DOMAIN` | Cognito Hosted UI domain |
| `NEXT_PUBLIC_COGNITO_CLIENT_ID` | Cognito app client ID |
| `NEXT_PUBLIC_SCHOOL_NAME` | Tùy chọn |

Giá trị thật chỉ nằm trong GitHub Variables/AWS, không ghi vào repository.

## IAM cần có

GitHub role cần quyền giới hạn cho `s3:PutObject` trên `frontend/releases/*`, `ssm:SendCommand` với document `AWS-RunShellScript` và đúng EC2 instance, cùng `ssm:GetCommandInvocation` để workflow chờ trạng thái lệnh.

EC2 instance role cần `s3:GetObject` trên `frontend/releases/*`.

Trust policy OIDC phải khóa repository `flwndyy/new-ai-exam-maker`, workflow `Deploy frontend` và branch `main`.

## Rollback

Mỗi release được đặt tại `/var/www/exammaker-frontend/releases/<release-id>` và Nginx phục vụ qua symlink `/var/www/exammaker-frontend/current`. Script giữ tối đa 5 release gần nhất. Nếu tải, kiểm checksum, Nginx hoặc kiểm tra asset thất bại, symlink được trả về release trước đó và Nginx được reload lại.

## Chạy thử

1. Tạo/cập nhật các GitHub Variables.
2. Push workflow lên `main` hoặc chọn **Actions → Deploy frontend → Run workflow**.
3. Kiểm tra job `Deploy frontend` và log SSM.
4. Xác minh URL public trả HTTP 200 và `/_next/static/...` không trả 404.
5. Sau khi chạy ổn, tạo PR O7 để nhóm review workflow, script và policy mẫu.
