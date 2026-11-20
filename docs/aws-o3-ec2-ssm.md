# O3 — EC2, EBS, Elastic IP, SSM và IAM

Tài liệu này hướng dẫn tạo EC2 cho AI Exam Maker trên AWS Console. Tài liệu không tự
thao tác AWS và không chứa ID/ARN/secret thật. Thực hiện sau khi O2 đã tạo VPC, subnet
và Security Group.

## 1. Giá trị đã chốt

| Thành phần | Giá trị |
|---|---|
| Region | `ap-southeast-1` |
| AMI | Amazon Linux 2023, ARM64 |
| Instance type | `t4g.small` |
| Public subnet | `aem-public-a` |
| Security Group | `aem-ec2-sg` |
| Root volume | EBS gp3, 30 GiB, encrypted |
| Instance profile | `aem-exammaker-ec2-role` |
| Elastic IP tag | `aem-exammaker-eip` |

Không dùng `t3.small` hoặc AMI x86_64 vì backend được publish cho `linux-arm64`.

## 2. Chuẩn bị instance profile

Thực hiện trong **IAM → Roles → Create role**:

1. Chọn trusted entity **AWS service → EC2**.
2. Đặt tên role `aem-exammaker-ec2-role`.
3. Gắn managed policy `AmazonSSMManagedInstanceCore` để SSM Agent đăng ký node và
   mở Session Manager. Không gắn `AdministratorAccess`, `IAMFullAccess` hoặc quyền Billing.
4. Tạo policy inline/customer theo
   [`infra/iam/exammaker-ec2-policy.example.json`](../infra/iam/exammaker-ec2-policy.example.json).
5. Thay các placeholder bằng ARN thật của queue, hai model Bedrock, inference profile,
   Cognito User Pool, Parameter Store path và CloudWatch log group **chỉ trong AWS Console
   hoặc secret manager**; không sửa file mẫu rồi commit lại.
6. Nếu SQS/Cognito/Parameter Store chưa được tạo bởi OPS-2, chỉ gắn
   `AmazonSSMManagedInstanceCore` ở O3; bổ sung policy runtime sau O9–O12.

Policy runtime chỉ cấp các thao tác app cần: SQS enqueue/consume/retry trên queue chính,
gọi hai model Bedrock, quản trị user và đọc group trong đúng Cognito User Pool, đọc đúng
nhánh Parameter Store, ghi log vào log group và gửi metric vào namespace đã chỉ định. DLQ
không nằm trong quyền của app; SQS redrive tự chuyển message sang DLQ. SSM Session Manager
là quyền quản trị máy; không dùng access key trên EC2.

### 2.1 Bedrock model và inference profile

Parameter Store `ModelId` có thể chứa một foundation model ID cho gọi in-region hoặc một
inference profile ID có tiền tố `apac.` cho cross-Region inference. Policy mẫu có placeholder
riêng cho Haiku và Sonnet v2, inference profile ARN của từng model, cùng foundation-model ARN
ở source và các destination Region. Khi dùng profile `apac.*`, điền đúng các destination
Region mà Bedrock liệt kê cho profile; không dùng ARN của profile khác model. Không bật
`InvokeModelWithResponseStream` vì worker hiện gọi bất đồng bộ và không stream.

### 2.2 CloudWatch

Điền `CLOUDWATCH_LOG_GROUP_ARN` bằng ARN log group của API/Worker và
`CLOUDWATCH_METRIC_NAMESPACE` bằng namespace cố định của ứng dụng. `PutMetricData` bắt buộc
dùng `Resource: "*"` vì CloudWatch Metrics không hỗ trợ giới hạn resource ARN; điều kiện
namespace là giới hạn quyền còn lại. Nếu O13/O15 chưa tạo log group hoặc metric heartbeat,
chỉ cấp statement này sau khi đã chốt tên log group/namespace.

## 3. Tạo EC2

Vào **EC2 → Instances → Launch instance**:

1. Name: `aem-exammaker-ec2`.
2. Chọn Amazon Linux 2023 **ARM64**.
3. Instance type: `t4g.small`.
4. Network:
   - VPC: `aem-demo-vpc`;
   - Subnet: `aem-public-a`;
   - Auto-assign public IPv4: tắt nếu sẽ gắn Elastic IP;
   - Security Group: chọn `aem-ec2-sg`.
5. IAM instance profile: chọn `aem-exammaker-ec2-role`.
6. Storage: root volume `30 GiB`, type `gp3`, **encrypted**. Dùng AWS managed key nếu
   team chưa có yêu cầu KMS key riêng.
7. Không cần tạo hoặc tải key pair vì O3 dùng SSM, không SSH.
8. User data: có thể để trống khi launch. Sau khi xác nhận SSM online, chạy bootstrap O1
   qua Session Manager; không bật Nginx trước khi O4 cài cấu hình HTTP-only.
9. Launch instance và ghi lại Instance ID ở ghi chú riêng, không đưa vào GitHub.

## 4. Cấp Elastic IP

1. Vào **EC2 → Elastic IPs → Allocate Elastic IP address**.
2. Chọn **IPv4 address pool: Amazon's pool of IPv4 addresses**, tag `aem-exammaker-eip`.
3. **Actions → Associate Elastic IP address**, chọn instance `aem-exammaker-ec2`.
4. Ghi lại địa chỉ IP trong ghi chú riêng của team. Không commit IP vào repository.
5. Xác nhận Security Group vẫn không mở SSH; Elastic IP không thay đổi rule inbound.

## 5. Kiểm tra SSM bằng Console

1. Vào **Systems Manager → Fleet Manager → Managed nodes**.
2. Chờ instance xuất hiện với ping status **Online**. Nếu chưa online, kiểm tra role,
   outbound route của public subnet và trạng thái SSM Agent.
3. Vào **Session Manager → Start session**, chọn instance và mở shell.
4. Chạy các lệnh kiểm tra:

   ```bash
   uname -m
   cat /etc/os-release
   df -h /
   systemctl is-active amazon-ssm-agent
   ```

   Kết quả mong đợi: kiến trúc `aarch64`, Amazon Linux 2023, root volume khoảng 30 GiB,
   và SSM Agent `active`.

5. Sau khi O2/O3 đã xác minh, chạy nội dung bootstrap đã review từ
   `infra/bootstrap/user-data.sh.example` bằng Session Manager. Bootstrap chỉ cài
   prerequisite và giữ Nginx stopped; O4 mới cài ACME/Nginx.

## 6. Checklist O3

- [ ] EC2 là Amazon Linux 2023 ARM64, type `t4g.small`.
- [ ] EC2 ở `aem-public-a`, dùng `aem-ec2-sg`.
- [ ] Root EBS là gp3 30 GiB và encrypted.
- [ ] Instance profile có trust EC2 và `AmazonSSMManagedInstanceCore`.
- [ ] Policy runtime dùng ARN thật chỉ trong AWS, không commit vào repo.
- [ ] Không có inbound SSH port 22.
- [ ] Elastic IP đã associate đúng instance.
- [ ] Instance xuất hiện Online trong Systems Manager.
- [ ] Đã mở được Session Manager shell.
- [ ] Đã ghi Instance ID và Elastic IP ở nơi riêng được kiểm soát.
- [ ] Đã dừng EC2 khi không làm việc; khi kết thúc demo phải release Elastic IP và dọn tài nguyên theo O16.

Nếu SSM chưa Online, không chuyển sang cài ứng dụng bằng SSH. Dừng tại bước kiểm tra
instance profile, subnet route và outbound Security Group.
