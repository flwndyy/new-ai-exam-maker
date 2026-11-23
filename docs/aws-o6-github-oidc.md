# O6 — GitHub Actions OIDC và deploy role

Tài liệu này tạo nền tảng xác thực không dùng access key dài hạn cho GitHub
Actions. O6 chỉ tạo OIDC provider và IAM role; workflow deploy frontend thuộc O7.

## 1. GitHub OIDC provider

Trong IAM của AWS account dự án:

1. Mở **IAM → Identity providers → Add provider**.
2. Provider type: **OpenID Connect**.
3. Provider URL: `https://token.actions.githubusercontent.com`.
4. Audience: `sts.amazonaws.com`.
5. Tạo provider.

Chỉ tạo một provider cho account. Không commit Account ID hoặc ARN thật vào repo.

## 2. IAM role

Tạo role tên ví dụ `ExamMakerGitHubActionsDeploy` và chọn **Web identity**.

- Identity provider: `token.actions.githubusercontent.com`.
- Audience: `sts.amazonaws.com`.
- Sau khi tạo role, thay trust policy bằng mẫu tại
  `infra/iam/github-actions-oidc-trust-policy.json`.
- Thay `<ACCOUNT_ID>` bằng Account ID ngay trong IAM Console, không sửa file trong
  repository.

Trust policy bắt buộc giới hạn đồng thời:

- Repository: `flwndyy/new-ai-exam-maker`.
- Branch: `main`.
- Audience: `sts.amazonaws.com`.

Vì vậy pull request và branch khác không thể assume role deploy production.

## 3. Quyền của role

Gắn policy riêng cho đúng tài nguyên frontend sau khi O5 có bucket và distribution.
Không dùng `AdministratorAccess`.

Các quyền O7 dự kiến cần cấp:

- S3: `ListBucket` trên đúng bucket frontend.
- S3 object: `PutObject`, `GetObject`, `DeleteObject` trên đúng prefix frontend.
- CloudFront: `CreateInvalidation` trên đúng distribution.

Giới hạn resource bằng ARN thật trong IAM Console; không ghi ARN thật vào repo.

## 4. Kiểm tra an toàn

- [ ] Provider URL và audience đúng.
- [ ] `sub` chỉ cho phép `repo:flwndyy/new-ai-exam-maker:ref:refs/heads/main`.
- [ ] Role không có `AdministratorAccess`.
- [ ] Không có access key, secret, Account ID, ARN thật hoặc bucket thật trong Git.
- [ ] Chưa bật deploy tự động trong O6; workflow deploy làm ở O7.

## 5. Bàn giao sang O7

Ghi các giá trị thật ngoài repository hoặc trong GitHub Actions Variables/Secrets
theo quy định của nhóm:

- Role ARN.
- Tên bucket frontend.
- CloudFront distribution ID.

O7 dùng `aws-actions/configure-aws-credentials` với OIDC và không dùng
`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`.
