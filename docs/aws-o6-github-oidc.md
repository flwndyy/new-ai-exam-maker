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

OIDC provider là tài nguyên dùng chung ở cấp AWS account. O6 tạo role riêng cho
frontend, tên ví dụ `aem-github-frontend-deploy-role`; O15 sẽ tạo role backend
riêng với quyền EC2/SSM theo nguyên tắc least privilege.

Trong IAM → **Roles → Create role → Web identity**:

- Identity provider: `token.actions.githubusercontent.com`.
- GitHub organization: `flwndyy`.
- GitHub repository: `new-ai-exam-maker`.
- GitHub branch: `main`.
- Audience: `sts.amazonaws.com`.
- Sau khi tạo role, thay trust policy bằng mẫu tại
  `infra/iam/github-actions-oidc-trust-policy.example.json`.
- Thay `${AWS_ACCOUNT_ID}` bằng Account ID ngay trong IAM Console, không sửa file trong
  repository.

Đối chiếu JSON sau khi wizard tạo role. Với policy khóa theo `ref:refs/heads/main`,
workflow O7 phải dùng **Repository Variables/Secrets**, không được khai báo
`environment:` trong job deploy, phải có tên workflow là **`Deploy frontend`** và
phải dùng file `.github/workflows/deploy-frontend.yml` theo quy ước O7. Trust
policy hiện tại chỉ khóa claim `workflow` theo **tên workflow** và claim `ref` theo
`main`; nó không kiểm tra đường dẫn file. Vì vậy không workflow nào khác trong repo
được đặt tên `Deploy frontend`. Nếu sau này O7 chuyển sang reusable workflow, có thể
thay claim `workflow` bằng `job_workflow_ref` sau khi chạy thử và xác minh claim
trong token thực tế. Nếu dùng GitHub Environment, phải đổi trust policy
để cho phép claim `sub` dạng `repo:flwndyy/new-ai-exam-maker:environment:<name>`
và giới hạn environment đó chỉ chạy từ `main`.

Trust policy bắt buộc giới hạn đồng thời:

- Repository: `flwndyy/new-ai-exam-maker`.
- Branch: `main`.
- Audience: `sts.amazonaws.com`.
- Workflow name claim: `Deploy frontend`.
- Workflow file convention: `.github/workflows/deploy-frontend.yml` trên `main`
  (không phải điều kiện mà policy hiện tại kiểm tra).

Vì vậy pull request và branch khác không thể assume role deploy production.

## 3. Quyền của role

Gắn policy riêng cho đúng tài nguyên frontend sau khi O5 có bucket và distribution.
Không dùng `AdministratorAccess`.

Các quyền O7 dự kiến cần cấp:

- S3: `ListBucket` trên đúng bucket frontend.
- S3 object: `PutObject`, `GetObject`, `DeleteObject` trên toàn bộ object của
  bucket frontend (`arn:aws:s3:::<bucket-name>/*`), vì O5 upload build vào root
  bucket (`index.html`, `/_next/static/*`, `/assets/*`).
- CloudFront: `CreateInvalidation` trên đúng distribution.

Policy mẫu tại `infra/iam/github-actions-frontend-deploy-policy.example.json` dùng
placeholder `${FRONTEND_BUCKET_ARN}`, `${FRONTEND_BUCKET_OBJECT_ARN}` và
`${CLOUDFRONT_DISTRIBUTION_ARN}`:

- `${FRONTEND_BUCKET_ARN}` = `arn:aws:s3:::<bucket-name>` — không có `/*`, dùng cho
  `s3:ListBucket`.
- `${FRONTEND_BUCKET_OBJECT_ARN}` = `arn:aws:s3:::<bucket-name>/*` — có `/*`, dùng
  cho `PutObject`, `GetObject` và `DeleteObject`.
- `${CLOUDFRONT_DISTRIBUTION_ARN}` =
  `arn:aws:cloudfront::<account-id>:distribution/<distribution-id>` — không có
  region và phải gồm tiền tố `distribution/`.

Thay placeholder bằng ARN thật trực tiếp trong IAM Console; không ghi ARN thật vào
repo.

## 4. Kiểm tra an toàn

- [ ] Provider URL và audience đúng.
- [ ] `sub` chỉ cho phép `repo:flwndyy/new-ai-exam-maker:ref:refs/heads/main`.
- [ ] Claim `workflow` là `Deploy frontend` và claim `ref` là `refs/heads/main`.
- [ ] Không workflow nào khác trong repository được đặt tên `Deploy frontend`.
- [ ] Workflow O7 dùng Repository Variables/Secrets và không đặt `environment:`.
- [ ] Role không có `AdministratorAccess`.
- [ ] Không có access key, secret, Account ID, ARN thật hoặc bucket thật trong Git.
- [ ] Chưa bật deploy tự động trong O6; workflow deploy làm ở O7.

## 5. Bàn giao sang O7

Ghi các giá trị thật ngoài repository hoặc trong GitHub Actions Variables/Secrets
theo quy định của nhóm:

- Frontend role ARN (`aem-github-frontend-deploy-role`).
- Tên bucket frontend.
- CloudFront distribution ID.

O7 dùng `aws-actions/configure-aws-credentials` với OIDC và không dùng
`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`.
