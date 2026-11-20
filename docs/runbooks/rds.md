# Runbook — Triển khai & Khôi phục Amazon RDS PostgreSQL (Task O10)

Tài liệu hướng dẫn dựng, kiểm tra, bàn giao, chụp snapshot và **thử khôi phục (restore drill)** cho **Amazon RDS PostgreSQL 16** của dự án **AI Exam Maker** theo đúng kiến trúc và hạn mức ngân sách **$200** tại `docs/context.md` (mục 5.2, 5.3, 6, 10, 11, 12, 14).

> **Quy tắc bảo mật:** Tuyệt đối **không** commit mật khẩu DB, connection string, endpoint thật, AWS Account ID hay ARN thật vào repository, và **không** gửi mật khẩu qua nhóm chat công khai. Connection string được lưu dạng `SecureString` trên **AWS Systems Manager Parameter Store** (key AWS-managed `alias/aws/ssm`, chi phí **$0**).

---

## 1. Thông số kiến trúc chuẩn & Kiểm soát chi phí

| Thành phần | Giá trị chuẩn | Tham chiếu |
|---|---|---|
| Region | `ap-southeast-1` (Singapore) | `docs/context.md` §14 |
| Engine | **PostgreSQL 16** (khớp với CI & integration test `A2`) | `backend/README.md` |
| Instance class | **`db.t4g.micro`** (ARM64 Graviton, $0.020/giờ) | `docs/context.md` §6, §14 |
| Triển khai (AZ) | **Single-AZ** (`MultiAZ = false` — cấm Multi-AZ dưới trần $200) | `docs/context.md` §1, §5.2 |
| Mạng & Truy cập | **Private subnet** (`PubliclyAccessible = false`), DB Subnet Group trên 2 AZ (`aem-private-a`, `aem-private-b`) | `docs/aws-o2-vpc-security-groups.md` |
| Security Group | `aem-rds-sg` — chỉ mở **TCP 5432 từ `aem-ec2-sg`**, cấm `0.0.0.0/0` | `docs/aws-o2-vpc-security-groups.md` |
| Lưu trữ | **`gp3` 20 GB** (autoscaling trần 40 GB để tránh đầy đĩa đột ngột) | `docs/context.md` §6 |
| Mã hóa lưu trữ (at rest) | **Bật** (`StorageEncrypted = true`), dùng key AWS-managed `alias/aws/rds` (**$0**) | `docs/context.md` §5.2, §10 |
| Mã hóa truyền tải (in transit) | **Bật** (`rds.force_ssl = 1` mặc định trên PG 16; Npgsql dùng `SSL Mode=Require`) | `docs/context.md` §10 |
| Automated backups | Retention **3 ngày** (ngắn để nằm gọn trong 20 GB free backup storage), cửa sổ `13:00-13:30` UTC (`20:00-20:30` giờ VN, trước giờ dừng 23:00) | `docs/context.md` §6, §12 |
| Giám sát nâng cao | Tắt Performance Insights & Enhanced Monitoring (`MonitoringInterval = 0`) để không phát sinh phí CloudWatch | `docs/context.md` §6 |
| Bảo vệ xoá (`DeletionProtection`) | `false` cho dev; có thể bật `true` trước demo để chống xoá nhầm | `infra/rds/setup-rds.sh` |
| Hạn mức instance ($200) | **Duy nhất 1 instance** dùng chung cho dev và demo (`exammaker-dev-pg`). Cấm tạo thêm instance song song để không vượt trần ngân sách | `docs/context.md` §6, §11 |
| Dự toán chi phí (2 tháng) | Compute `$19.20` (480 giờ/tháng, 07:00–23:00) + Storage/Snapshot `$6.48` = **`$25.68`** | `docs/context.md` §6 |

---

## 2. Điều kiện tiên quyết (Phụ thuộc Task `O2`)

Trước khi chạy script hoặc dựng trên Console, tài khoản AWS ở region **`ap-southeast-1`** phải có sẵn các tài nguyên mạng từ Task `O2` (`docs/aws-o2-vpc-security-groups.md`):

- VPC: `aem-demo-vpc` (`10.20.0.0/16`)
- 2 Private subnets ở 2 AZ khác nhau (route table chỉ có `local`, không có IGW/NAT):
  - `aem-private-a` (`10.20.11.0/24`)
  - `aem-private-b` (`10.20.12.0/24`)
- RDS Security Group: `aem-rds-sg` (chỉ có 1 rule inbound TCP `5432` với nguồn là Security Group `aem-ec2-sg`)

---

## 3. Cách 1: Dựng tự động bằng AWS CLI script (Khuyên dùng)

**Yêu cầu công cụ:** `aws` (CLI v2), `jq`, `openssl` — cả 3 đều **được cài sẵn trong AWS CloudShell** (`ap-southeast-1`). Script cũng đã tích hợp `MSYS_NO_PATHCONV=1` cho Git Bash trên Windows.

```bash
# Môi trường chuẩn (Single-instance, DeletionProtection=false)
./infra/rds/setup-rds.sh

# Bật DeletionProtection để bảo vệ trước buổi demo:
DELETION_PROTECTION=true ./infra/rds/setup-rds.sh
```

> [!WARNING]
> **Quy tắc ngân sách $200:** Ngân sách dự án chỉ tính cho **duy nhất 1 instance RDS**. Tuyệt đối không chạy thêm `ENV_NAME=prod` cùng lúc với `dev` (sẽ nhân đôi chi phí compute/storage lên ~$51, có nguy cơ vượt hạn mức $200). Môi trường dev và demo dùng chung instance `exammaker-dev-pg`.

**Hành vi create-if-absent & an toàn mật khẩu:**
- Tự động tìm `aem-demo-vpc`, `aem-private-a`, `aem-private-b`, `aem-rds-sg` theo tên (có thể ghi đè qua biến `VPC_NAME`, `PRIVATE_SUBNET_NAMES`, `RDS_SG_NAME`, hoặc truyền trực tiếp `SUBNET_IDS` và `RDS_SG_ID`).
- **An toàn mật khẩu:** Nếu tạo mới, script sinh mật khẩu ngẫu nhiên 32 ký tự chữ-số và **lưu ngay vào SSM Parameter Store dạng `SecureString` (`${SSM_PREFIX}/Rds/MasterPassword`) TRƯỚC KHI gọi `create-db-instance`**. Điều này đảm bảo mật khẩu không bao giờ bị mất nếu phiên CloudShell bị timeout hay rớt mạng giữa chừng khi đang chờ RDS khởi tạo.
- Đợi RDS `available` (~4–7 phút), lưu `${SSM_PREFIX}/ConnectionStrings/ExamMaker` dạng **`SecureString`** (không in mật khẩu ra màn hình) và 5 metadata key dưới `${SSM_PREFIX}/Rds/*`.
- Nếu chạy lại trên instance đã tồn tại: script đọc lại `${SSM_PREFIX}/Rds/MasterPassword` từ SSM để đảm bảo connection string luôn đầy đủ và không ghi đè làm mất mật khẩu DB. Để kiểm tra độ sai lệch cấu hình (drift), hãy chạy `./infra/rds/verify-rds.sh`.

---

## 4. Cách 2: Dựng thủ công trên AWS Console

1. **Tạo DB Subnet Group** (*RDS → Subnet groups → Create DB subnet group*):
   - Name: `exammaker-dev-db-subnets`.
   - VPC: `aem-demo-vpc`.
   - Subnets: chọn **`aem-private-a`** và **`aem-private-b`** (2 AZ khác nhau).
2. **Tạo Database** (*RDS → Databases → Create database*):
   - Creation method: **Standard create** · Engine: **PostgreSQL** · Version: **16.x** (mới nhất trong dòng 16).
   - Templates: **Free tier** hoặc **Dev/Test** (chủ động kiểm tra lại các mục bên dưới để đảm bảo Single-AZ).
   - Availability and durability: **Single-AZ DB instance deployment**.
   - DB instance identifier: `exammaker-dev-pg`.
   - Master username: `exammaker_admin` · đặt mật khẩu mạnh 24–32 ký tự chữ + số (tránh ký tự `;`, `=`, `@`, `/`, `"`).
   - Instance configuration: Burstable classes → **`db.t4g.micro`**.
   - Storage: **`gp3`**, Allocated storage **`20` GiB**, Enable storage autoscaling max **`40` GiB**.
   - Connectivity:
     - VPC: `aem-demo-vpc`
     - DB subnet group: `exammaker-dev-db-subnets`
     - Public access: **No**
     - VPC security group: chọn duy nhất **`aem-rds-sg`** (bỏ `default`).
   - Database authentication: **Password authentication**.
   - Monitoring: **Tắt** Performance Insights và **Tắt** Enhanced monitoring.
   - Additional configuration:
     - Initial database name: **`exammaker`**
     - Automated backups: **Bật**, Retention period **`3` days**, Backup window `13:00-13:30` UTC, bật **Copy tags to snapshots**.
     - Encryption: **Enable encryption** với AWS-managed key `(default) aws/rds`.
     - Deletion protection: **Tắt** cho `dev` (hoặc **Bật** trước ngày demo).
3. **Lưu cấu hình vào SSM Parameter Store** (*Systems Manager → Parameter Store → Create parameter*):
   - `${SSM_PREFIX}/Rds/MasterPassword` — Type: **`SecureString`** (KMS key mặc định `alias/aws/ssm`), Value: `<mat-khau>`
   - `${SSM_PREFIX}/ConnectionStrings/ExamMaker` — Type: **`SecureString`** (KMS key mặc định `alias/aws/ssm`), Value:
     `Host=<endpoint>;Port=5432;Database=exammaker;Username=exammaker_admin;Password=<mat-khau>;SSL Mode=Require`
   - Các metadata dạng `String` (Standard, miễn phí):
     - `${SSM_PREFIX}/Rds/InstanceId` = `exammaker-dev-pg`
     - `${SSM_PREFIX}/Rds/Endpoint` = `<endpoint>`
     - `${SSM_PREFIX}/Rds/Port` = `5432`
     - `${SSM_PREFIX}/Rds/Database` = `exammaker`
     - `${SSM_PREFIX}/Rds/Username` = `exammaker_admin`

---

## 5. Quy ước bàn giao cho DEV-A (`A1`/`A2`) & EC2 (`O3`)

- **Backend (.NET 10):** `backend/src/ExamMaker.Infrastructure/ParameterStoreConfiguration.cs` khi đặt `ParameterStore__Path=/exammaker/<env>` sẽ tự động ánh xạ `${SSM_PREFIX}/ConnectionStrings/ExamMaker` vào `ConnectionStrings:ExamMaker` (`ConnectionStrings__ExamMaker` theo `backend/README.md`).
- **Quyền IAM tối thiểu cho EC2 Instance Profile (Task `O3`):**
  - Xem định nghĩa mẫu tại [`infra/iam/exammaker-ec2-policy.example.json`](../../infra/iam/exammaker-ec2-policy.example.json) (statement `ReadConfiguredParameters`).
  - EC2 chỉ cần quyền `ssm:GetParametersByPath`, `ssm:GetParameter`, `ssm:GetParameters` trên `arn:aws:ssm:ap-southeast-1:<account-id>:parameter/exammaker/<env>/*`.
  - Với key AWS-managed `alias/aws/ssm`, key policy mặc định của AWS đã cho phép tất cả principal trong tài khoản giải mã khi gọi qua dịch vụ SSM (`kms:ViaService`), do đó **không cần** thêm quyền `kms:Decrypt` riêng trong IAM policy của EC2.
  - **Không** cấp quyền `rds:*` cho EC2 runtime (ứng dụng kết nối bảo mật qua mạng nội bộ TCP 5432 bằng connection string).
- **Ghi nhận nợ kỹ thuật (Least Privilege - B1):** Hiện tại connection string đang dùng master user `exammaker_admin`. Để tuân thủ least-privilege triệt để trong tương lai, nên tạo role ứng dụng riêng `exammaker_app` (owner của DB schema để chạy migration bundle nhưng không có `rds_superuser`).

---

## 6. Quy trình kiểm thử (Verification)

### 6.1 Kiểm tra tự động 15 tiêu chí bảo mật, mạng & ngân sách

```bash
# Môi trường dev (Single-instance)
./infra/rds/verify-rds.sh
```

Script xác minh đầy đủ **15 tiêu chí**:
1. SSM `SecureString` `${SSM_PREFIX}/ConnectionStrings/ExamMaker` tồn tại, đúng kiểu `SecureString`, có `SSL Mode=Require`, và khớp chính xác `Host`/`Port`/`Database`/`Username` với RDS thực tế và metadata dưới `${SSM_PREFIX}/Rds/*`.
2. Engine là `postgres` dòng `16.x`.
3. Instance class là `db.t4g.micro`.
4. Triển khai Single-AZ (`MultiAZ = false`).
5. Không công khai (`PubliclyAccessible = false`).
6. Mã hóa đĩa (`StorageEncrypted = true`).
7. Loại ổ đĩa `gp3`, dung lượng ban đầu 20 GB và trần autoscaling `≤ 40 GB` (bảo vệ ngân sách).
8. Automated backup retention ngắn (`1–7` ngày).
9. `CopyTagsToSnapshot = true`.
10. DB Subnet Group trải trên `≥ 2` AZ và **không** có subnet nào route `0.0.0.0/0` hoặc `::/0` ra Internet Gateway (`igw-*`, `eigw-*`) hoặc đi qua NAT Gateway.
11. Security Group chỉ mở duy nhất TCP `5432` từ nguồn là Security Group `aem-ec2-sg`, không có rule CIDR / `0.0.0.0/0` / `::/0` và không có Prefix List.
12. Performance Insights và Enhanced Monitoring đều tắt (tránh phát sinh chi phí CloudWatch).
13. `rds.force_ssl = 1` đang có hiệu lực trong parameter group.
14. `DeletionProtection` khớp chính sách môi trường.
15. Trạng thái instance là `available`.

### 6.2 Kiểm tra kết nối & chạy migration `A2` từ EC2 (sau khi có Task `O3`)

Vì RDS nằm trong private subnet, không thể kết nối trực tiếp từ máy cá nhân. Mở **SSM Session Manager** vào EC2 (`aem-ec2-sg`) rồi thực hiện:

```bash
# 1. Cài đặt PostgreSQL 16 client trên Amazon Linux 2023
sudo dnf install -y postgresql16

# 2. Lấy connection string hoặc password từ SSM vào biến phiên tạm (không ghi ra file, không lộ vào shell history)
export ConnectionStrings__ExamMaker="$(aws ssm get-parameter --region ap-southeast-1 \
  --name /exammaker/dev/ConnectionStrings/ExamMaker --with-decryption \
  --query Parameter.Value --output text)"

export PGPASSWORD="$(aws ssm get-parameter --region ap-southeast-1 \
  --name /exammaker/dev/Rds/MasterPassword --with-decryption \
  --query Parameter.Value --output text 2>/dev/null || \
  aws ssm get-parameter --region ap-southeast-1 \
  --name /exammaker/dev/ConnectionStrings/ExamMaker --with-decryption \
  --query Parameter.Value --output text | sed -n 's/.*Password=\([^;]*\).*/\1/p')"

# 3. Chạy EF Core migration bundle (hoặc dotnet ef database update trên máy build)
# 4. Xác nhận dữ liệu seed A2 (kỳ vọng: departments=4, subjects=4, topics=82)
DB_HOST="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Rds/Endpoint --query Parameter.Value --output text)"
DB_USER="$(aws ssm get-parameter --region ap-southeast-1 --name /exammaker/dev/Rds/Username --query Parameter.Value --output text)"

psql "host=${DB_HOST} port=5432 dbname=exammaker user=${DB_USER} sslmode=require" \
  -c "SELECT 'departments' AS table_name, COUNT(*) FROM departments
      UNION ALL SELECT 'subjects', COUNT(*) FROM subjects
      UNION ALL SELECT 'topics', COUNT(*) FROM topics;"
```
```

---

## 7. Quy trình chụp Snapshot & Thử khôi phục (Restore Drill — bắt buộc theo mục 12)

Theo `docs/context.md` §11–§12 và yêu cầu Issue #22:
1. **Chụp snapshot thủ công trước mỗi migration lớn.**
2. **Thực hiện thử khôi phục ít nhất 1 lần trước buổi demo** trên một instance tạm (tuyệt đối không ghi đè DB đang chạy).
3. **Phân định hai mức kiểm thử khôi phục:**
   - **Chặng 1 (O10):** Đã chạy thành công kịch bản thử khôi phục tự động ở tầng hạ tầng (**Infrastructure Restore Drill**) qua `./infra/rds/restore-drill.sh`: tự động chụp snapshot -> restore instance tạm -> xác minh 100% guardrails (Single-AZ, private, storage encrypted, available) -> tự động dọn dẹp an toàn qua `EXIT` trap để không phát sinh chi phí.
   - **Chặng 3 (O16):** Thực hiện kiểm thử toàn vẹn dữ liệu ứng dụng (**Data Integrity Verification**) từ EC2 (O3) qua SSM Session Manager sau khi đã triển khai migration bundle A2 và seed data.

> [!CAUTION]
> **Cảnh báo chống mất dữ liệu:** Lệnh `aws rds delete-db-instance --skip-final-snapshot` **CHỈ ĐƯỢC PHÉP ÁP DỤNG cho instance tạm `exammaker-dev-pg-restore-test`**. TUYỆT ĐỐI KHÔNG chạy lệnh này trên instance chính `exammaker-dev-pg`!

### Cách khuyên dùng: Chạy script tự động có cơ chế bảo vệ ngân sách (Trap Cleanup)

```bash
# Chạy toàn bộ quy trình drill và tự động dọn dẹp sạch sẽ
./infra/rds/restore-drill.sh

# Hoặc tạm dừng trước khi dọn dẹp để kết nối kiểm tra thủ công qua EC2
KEEP_FOR_MANUAL_CHECK=1 ./infra/rds/restore-drill.sh
```

### Các bước thao tác chi tiết (tham chiếu):

#### Bước 1: Chụp snapshot thủ công

```bash
ENV_NAME="${ENV_NAME:-dev}"
DB_ID="exammaker-${ENV_NAME}-pg"
SNAP_ID="${DB_ID}-drill-$(date -u +%Y%m%d%H%M)"

aws rds create-db-snapshot \
  --region ap-southeast-1 \
  --db-instance-identifier "$DB_ID" \
  --db-snapshot-identifier "$SNAP_ID"

aws rds wait db-snapshot-available \
  --region ap-southeast-1 \
  --db-snapshot-identifier "$SNAP_ID"
echo "Snapshot ready: ${SNAP_ID}"
```

#### Bước 2: Khôi phục ra instance tạm trong cùng private subnet & SG

```bash
RESTORE_ID="${DB_ID}-restore-test"
SUBNET_GROUP="exammaker-${ENV_NAME}-db-subnets"
RDS_SG_ID="$(aws rds describe-db-instances --region ap-southeast-1 \
  --db-instance-identifier "$DB_ID" \
  --query "DBInstances[0].VpcSecurityGroups[0].VpcSecurityGroupId" --output text)"

aws rds restore-db-instance-from-db-snapshot \
  --region ap-southeast-1 \
  --db-instance-identifier "$RESTORE_ID" \
  --db-snapshot-identifier "$SNAP_ID" \
  --db-instance-class db.t4g.micro \
  --db-subnet-group-name "$SUBNET_GROUP" \
  --vpc-security-group-ids "$RDS_SG_ID" \
  --no-multi-az \
  --no-publicly-accessible \
  --no-deletion-protection

aws rds wait db-instance-available \
  --region ap-southeast-1 \
  --db-instance-identifier "$RESTORE_ID"
```

#### Bước 3: Xác minh instance khôi phục

```bash
aws rds describe-db-instances \
  --region ap-southeast-1 \
  --db-instance-identifier "$RESTORE_ID" \
  --query "DBInstances[0].{Status:DBInstanceStatus,Encrypted:StorageEncrypted,Public:PubliclyAccessible,Endpoint:Endpoint.Address}" \
  --output table
```

*(Trong Chặng 3, sau khi có EC2 từ O3, kết nối từ EC2 qua SSM Session Manager tới Endpoint của `RESTORE_ID` và chạy lại câu `SELECT COUNT(*)` bảng `departments`/`subjects`/`topics`/`users` và bảng `__EFMigrationsHistory` để xác nhận dữ liệu nguyên vẹn 100%).*

#### Bước 4: Xoá ngay tài nguyên test để không tốn ngân sách

```bash
aws rds delete-db-instance \
  --region ap-southeast-1 \
  --db-instance-identifier "$RESTORE_ID" \
  --skip-final-snapshot \
  --delete-automated-backups

aws rds wait db-instance-deleted \
  --region ap-southeast-1 \
  --db-instance-identifier "$RESTORE_ID"

aws rds delete-db-snapshot \
  --region ap-southeast-1 \
  --db-snapshot-identifier "$SNAP_ID"
```

---

## 8. Vận hành tiết kiệm chi phí & Xử lý sự cố (Troubleshooting)

### Lịch tắt/bật hàng ngày (07:00–23:00 giờ VN)

```bash
# Dừng RDS lúc 23:00 (chỉ tính phí storage $2.76/tháng, không tính phí compute $0.020/giờ)
aws rds stop-db-instance --region ap-southeast-1 --db-instance-identifier exammaker-dev-pg

# Bật lại RDS lúc 07:00
aws rds start-db-instance --region ap-southeast-1 --db-instance-identifier exammaker-dev-pg
```

> **Lưu ý quan trọng của AWS:** RDS chỉ cho phép dừng tối đa **7 ngày liên tục** — sau 7 ngày AWS sẽ **tự động bật lại** để chạy bảo trì. Kiểm tra trạng thái nếu nghỉ dài ngày.

### Xử lý sự cố thường gặp

| Triệu chứng / Lỗi | Nguyên nhân phổ biến | Cách khắc phục |
|---|---|---|
| `VPC 'aem-demo-vpc' not found` khi chạy `setup-rds.sh` | Task `O2` chưa được dựng trên AWS Console hoặc chạy nhầm region (vd `us-east-1`). | Chuyển AWS CloudShell sang `ap-southeast-1` và hoàn tất `docs/aws-o2-vpc-security-groups.md` trước, hoặc truyền rõ `SUBNET_IDS` & `RDS_SG_ID`. |
| Không kết nối được RDS từ máy cá nhân (`timeout`) | Đúng thiết kế bảo mật: RDS nằm trong private subnet, không có IP public. | Chỉ kết nối từ EC2 trong `aem-ec2-sg` thông qua **SSM Session Manager** (hoặc dùng PostgreSQL Docker ở local theo `backend/README.md`). |
| Npgsql báo lỗi SSL / `no pg_hba.conf entry ... no encryption` | PostgreSQL 16 trên RDS bật mặc định `rds.force_ssl = 1`. | Đảm bảo connection string có `SSL Mode=Require` (đã được `setup-rds.sh` tự thêm). |
| `verify-rds.sh` báo `status is 'stopped'` | RDS đang được tắt qua đêm để tiết kiệm chi phí. | Chạy `aws rds start-db-instance` rồi chạy lại `./infra/rds/verify-rds.sh`. |
| Mất mật khẩu khi chạy lại `setup-rds.sh` trên DB cũ | Tham số SSM bị xoá tay trong khi RDS vẫn còn. | Đổi mật khẩu qua `aws rds modify-db-instance --master-user-password ... --apply-immediately` rồi chạy lại `DB_PASSWORD='<new-pass>' ./infra/rds/setup-rds.sh`. |
