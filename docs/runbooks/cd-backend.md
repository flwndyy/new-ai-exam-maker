# Runbook — CI/CD Backend (.NET 10 ARM64), EF Core Migration Bundle & Rollback (Task O15)

Tài liệu hướng dẫn thiết lập, vận hành và rollback pipeline **CI/CD Backend** của dự án **AI Exam Maker** theo `docs/context.md` (mục 5.2, 11) và `docs/tasks.md` (O15).

> **Quy tắc bảo mật bất biến:**
> - **Không dùng SSH**, **không dùng AWS access key dài hạn** trên GitHub Actions hay EC2.
> - GitHub Actions xác thực bằng **OIDC** (`aem-github-backend-deploy-role`), khóa chặt theo repo `flwndyy/new-ai-exam-maker`, nhánh `main` và tên workflow `Deploy backend`.
> - Không commit Account ID, ARN thật, EC2 Instance ID hay tên bucket thật vào Git; lưu trong **GitHub Repository Variables** (`AWS_BACKEND_ROLE_ARN`, `BACKEND_ARTIFACTS_BUCKET`).

---

## 1. Luồng triển khai tự động (`.github/workflows/deploy-backend.yml`)

Khi code trong `backend/**`, `infra/deploy/**` hoặc `.github/workflows/deploy-backend.yml` được merge vào nhánh **`main`** (hoặc chạy thủ công bằng **Actions → Deploy backend → Run workflow** trên nhánh `main`):

1. **Build & Publish self-contained `linux-arm64`:**
   - Publish cả `ExamMaker.Api` và `ExamMaker.Worker` với `-c Release -r linux-arm64 --self-contained true` (khớp EC2 `t4g.small` ARM64 và `infra/systemd/*.service.example`).
2. **Tạo EF Core Migration Bundle (`efbundle`):**
   - Dùng `dotnet-ef` `10.0.4` build file thực thi độc lập `efbundle` cho `linux-arm64` từ `ExamMaker.Infrastructure` (sử dụng `ExamMakerDbContextFactory`).
3. **Đóng gói & Lưu trữ Artifact:**
   - Nén `exammaker-backend-<release-id>.tar.gz` kèm checksum `.sha256` (gồm API, Worker, `efbundle` và `deploy-on-ec2.sh`).
   - Lưu bản sao lên **GitHub Artifacts** (30 ngày) và tải lên **S3 bucket riêng tư** (`s3://<bucket>/backend/releases/`, tự động hết hạn sau 30 ngày để không phát sinh phí lưu trữ).
4. **Chụp Snapshot RDS thủ công trước migration lớn (tùy chọn trên `workflow_dispatch`):**
   - Nếu bật tùy chọn **`create_rds_snapshot = true`** khi chạy `workflow_dispatch`, workflow tự động chụp manual snapshot `exammaker-dev-pre-mig-<sha>-<timestamp>` và chờ trạng thái `available` **trước khi** chạy migration.
5. **Triển khai không cần SSH qua AWS SSM Run Command (`infra/deploy/deploy-on-ec2.sh`):**
   - Trên EC2 (`aem-exammaker-ec2`): tải tarball từ S3, kiểm tra SHA-256, giải nén vào `/opt/exammaker/releases/<release-id>`.
   - **Chạy `efbundle` TRƯỚC khi đổi symlink:** đọc `/exammaker/dev/ConnectionStrings/ExamMaker` từ SSM Parameter Store vào biến môi trường `ConnectionStrings__ExamMaker` (không ghi xuống đĩa, không truyền qua đối số `argv` nên không lộ mật khẩu trong `ps`). Nếu migration lỗi → **dừng ngay**, `/opt/exammaker/current` vẫn trỏ bản cũ!
   - **Đổi symlink nguyên tử & restart systemd:** `ln -sfn /opt/exammaker/releases/<release-id> /opt/exammaker/current` → `systemctl restart exammaker-api exammaker-worker`.
   - **Kiểm tra Health & Auto-Rollback:** kiểm tra `http://127.0.0.1:5000/health` và trạng thái `active` của cả 2 service trong tối đa 30 giây. Nếu thất bại → **tự động trỏ symlink về release trước đó**, restart lại và báo job fail!
   - **Giữ N = 5 bản gần nhất:** tự động xóa các thư mục cũ hơn trong `/opt/exammaker/releases/` để bảo vệ dung lượng ổ EBS 30 GB.

---

## 2. Dựng hạ tầng O15 trên AWS & cấu hình GitHub Variables

### Bước 1: Chạy script dựng S3 artifacts bucket + OIDC Role + quyền EC2
```bash
./infra/deploy/setup-backend-cd.sh
```
Script tự động tạo/cập nhật:
- Bucket S3 riêng tư `aem-dev-backend-artifacts-<hash>` (Block Public Access, SSE-S3 AES256 = $0, chính sách TLS-only, vòng đời xóa sau 30 ngày).
- IAM Role OIDC `aem-github-backend-deploy-role` (trust policy theo mẫu [`infra/iam/github-actions-backend-oidc-trust-policy.example.json`](../../infra/iam/github-actions-backend-oidc-trust-policy.example.json), permission policy theo mẫu [`infra/iam/github-actions-backend-deploy-policy.example.json`](../../infra/iam/github-actions-backend-deploy-policy.example.json)).
- Inline policy `aem-ec2-read-backend-artifacts` (`s3:GetObject` chỉ trên `backend/releases/*`) cho role EC2 `aem-exammaker-ec2-role`.

### Bước 2: Xác minh 10 tiêu chí bảo mật & SSM
```bash
./infra/deploy/verify-backend-cd.sh
```

### Bước 3: Khai báo 2 GitHub Repository Variables (không dùng `environment:`)
Vào **GitHub Repo → Settings → Secrets and variables → Actions → Variables → New repository variable**:
- `BACKEND_ARTIFACTS_BUCKET`: tên bucket in ra ở Bước 1 (`aem-dev-backend-artifacts-...`).
- `AWS_BACKEND_ROLE_ARN`: ARN của role `aem-github-backend-deploy-role` (lấy trong AWS IAM Console).

*(Lưu ý: giống O6/O7, không khai báo `environment:` trong job `Deploy backend` để khớp điều kiện `sub = repo:flwndyy/new-ai-exam-maker:ref:refs/heads/main` của OIDC trust policy).*

### Checklist điều kiện tiên quyết trên EC2 trước lần chạy `Deploy backend` đầu tiên trên `main`
- Đã cài đủ 2 systemd unit `/etc/systemd/system/exammaker-api.service` và `/etc/systemd/system/exammaker-worker.service` (từ `infra/systemd/*.service.example`) kèm `/etc/exammaker/api.env` và `/etc/exammaker/worker.env` (chỉ định `ParameterStore__Path=/exammaker/dev` khớp `ParameterStoreConfiguration.cs` và `docs/runbooks/parameter-store.md` §4.1).
- Parameter Store đã có đủ `/exammaker/dev/Sqs/AiJobsQueueUrl` và `/exammaker/dev/Bedrock/ModelId` (đã dựng ở O11/O12). Nếu thiếu một trong hai khóa này, `exammaker-worker` ở môi trường Production sẽ chủ động **fail-fast** (`InvalidOperationException` trong `ExamMaker.Worker/Program.cs`) và `deploy-on-ec2.sh` sẽ tự động kích hoạt **auto-rollback** đúng như thiết kế.

---

## 3. Quy tắc Migration tương thích ngược & Chụp Snapshot RDS trước migration lớn

Theo `docs/context.md` §11:
1. **Expand-and-Contract (tương thích ngược):**
   - Mọi migration thêm cột mới phải là `NULLABLE` hoặc có `DEFAULT`, để nếu code mới gặp lỗi và auto-rollback về release cũ, code cũ vẫn chạy bình thường trên schema mới mà không vỡ DB.
   - Khi cần đổi tên hoặc xóa cột/bảng: tách thành 2 đợt release (đợt 1 bỏ tham chiếu trong code, đợt 2 mới drop cột).
2. **Chụp Snapshot RDS thủ công trước migration lớn:**
   - Cách 1: Khi merge PR có migration lớn vào `main`, chạy **Actions → Deploy backend → Run workflow** trên `main` và tick **`create_rds_snapshot = true`**.
   - Cách 2 (từ CloudShell/OPS):
     ```bash
     aws rds create-db-snapshot \
       --region ap-southeast-1 \
       --db-instance-identifier exammaker-dev-pg \
       --db-snapshot-identifier "exammaker-dev-pre-mig-$(date -u +%Y%m%d%H%M%S)"
     ```
   - Nhớ xóa manual snapshot cũ sau khi đã xác nhận ổn định (hoặc trong Task O16) để tiết kiệm chi phí lưu trữ snapshot ($0.095/GB-tháng).

---

## 4. Quy trình Rollback thủ công khẩn cấp

Nếu sau khi deploy phát hiện lỗi nghiệp vụ và cần lùi ngay về bản trước đó (vì EC2 giữ sẵn 5 bản gần nhất trong `/opt/exammaker/releases/`):

```bash
# 1. Xem danh sách các bản release đang lưu trên EC2 (mới nhất lên trên)
LIST_ONLY=1 ./infra/deploy/rollback-backend.sh

# 2. Rollback tự động về bản liền trước và kiểm tra /health
./infra/deploy/rollback-backend.sh

# 3. Hoặc rollback về một release ID cụ thể
TARGET_RELEASE="20261123150000-abcdef123456" ./infra/deploy/rollback-backend.sh
```
