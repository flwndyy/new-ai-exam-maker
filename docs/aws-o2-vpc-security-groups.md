# O2 — VPC, subnet và Security Group

Tài liệu này hướng dẫn thao tác O2 trên AWS Console cho môi trường demo. O2 không dùng
Terraform và không commit ID/ARN thật vào repository.

## 1. Giá trị đã chốt

| Thành phần | Giá trị |
|---|---|
| Region | `ap-southeast-1` |
| VPC name | `aem-demo-vpc` |
| VPC CIDR | `10.20.0.0/16` |
| Public subnet | `aem-public-a`, `10.20.1.0/24` |
| Private subnet 1 | `aem-private-a`, `10.20.11.0/24` |
| Private subnet 2 | `aem-private-b`, `10.20.12.0/24` |
| EC2 Security Group | `aem-ec2-sg` |
| RDS Security Group | `aem-rds-sg` |
| CloudFront prefix list | `com.amazonaws.global.cloudfront.origin-facing` |

Không dùng lại CIDR đang bị trùng với VPC khác mà tài khoản cần kết nối. Nếu phát hiện
trùng, dừng và báo team trước khi đổi CIDR; không tự đổi một phần vì subnet/route/SG phải
đồng bộ.

## 2. Tạo VPC và Internet Gateway

1. Chọn region `ap-southeast-1` ở góc phải AWS Console.
2. Mở **VPC → Your VPCs → Create VPC**.
3. Chọn **VPC only** để tránh wizard tự tạo NAT Gateway hoặc tài nguyên dư thừa.
4. Nhập:
   - Name tag: `aem-demo-vpc`;
   - IPv4 CIDR: `10.20.0.0/16`;
   - Tenancy: `Default`.
5. Tạo Internet Gateway tại **VPC → Internet Gateways → Create** với tên
   `aem-demo-igw`, sau đó **Attach to a VPC** và chọn `aem-demo-vpc`.
6. Trong VPC details, kiểm tra **DNS resolution** và **DNS hostnames** đang bật.

## 3. Tạo subnet

Chọn hai Availability Zone khác nhau trong `ap-southeast-1`.

1. Vào **VPC → Subnets → Create subnet**.
2. Tạo `aem-public-a`, CIDR `10.20.1.0/24` ở AZ thứ nhất.
3. Tạo `aem-private-a`, CIDR `10.20.11.0/24` ở AZ thứ nhất.
4. Tạo `aem-private-b`, CIDR `10.20.12.0/24` ở AZ thứ hai.
5. Không bật **Auto-assign public IPv4** cho private subnet.
6. Với public subnet, có thể để tắt auto-assign; O3 sẽ gắn Elastic IP cho EC2.

Hai private subnet ở hai AZ là cần thiết để tạo RDS DB subnet group ở O10, dù RDS MVP
chạy Single-AZ.

## 4. Tạo và gắn route table

### Public route table

1. Vào **VPC → Route Tables → Create route table**.
2. Tên: `aem-public-rt`, VPC: `aem-demo-vpc`.
3. Tab **Subnet associations** → associate `aem-public-a`.
4. Tab **Routes** → thêm:
   - `10.20.0.0/16` → `local` (AWS tạo sẵn);
   - `0.0.0.0/0` → Internet Gateway `aem-demo-igw`.

### Private route table

1. Tạo route table `aem-private-rt` trong `aem-demo-vpc`.
2. Associate cả `aem-private-a` và `aem-private-b`.
3. Chỉ giữ route local `10.20.0.0/16`.
4. Không tạo NAT Gateway và không thêm route `0.0.0.0/0` tới Internet Gateway.

> Nếu dùng wizard **VPC and more**, đặt **NAT gateways = None** và kiểm tra lại route table;
> cách an toàn hơn cho O2 là dùng **VPC only** như các bước trên.

## 5. Kiểm tra prefix list và quota

1. Vào **VPC → Managed Prefix Lists**.
2. Lọc **Owner ID = AWS** và tìm `com.amazonaws.global.cloudfront.origin-facing`.
3. Ghi nhận đúng prefix list IPv4. Không tự tạo hoặc sửa AWS-managed prefix list.
4. Vào **Service Quotas → Amazon VPC → Inbound or outbound rules per security group** để
   kiểm tra quota rule, không chọn **Security groups per VPC** (đó là quota số lượng SG).
   Prefix list CloudFront có weight 55, nên rule 443 chiếm 55 inbound IPv4 rule slots; rule
   HTTP 80 chiếm thêm 1 slot. Tổng cộng hai rule này dùng 56 inbound IPv4 slots.
5. Với quota mặc định 60 và Security Group chỉ có hai rule trên, quota là đủ. Nếu tài khoản
   có thêm rule khác làm vượt quota, dừng để xin tăng quota; không thay bằng danh sách IP
   CloudFront tự copy thủ công.

## 6. Tạo Security Group cho EC2

1. Vào **VPC → Security Groups → Create security group**.
2. Name: `aem-ec2-sg`; Description: `EC2 origin for AI Exam Maker`; VPC: `aem-demo-vpc`.
3. Inbound rules:

   | Type | Port | Source | Description |
   |---|---:|---|---|
   | HTTPS | 443 | AWS-managed prefix list `com.amazonaws.global.cloudfront.origin-facing` | CloudFront origin-facing only |
   | HTTP | 80 | `0.0.0.0/0` | Let's Encrypt HTTP-01 only |

4. Không thêm TCP 22/SSH.
5. Outbound giữ mặc định `All traffic → 0.0.0.0/0`; O3 cần EC2 gọi SSM/AWS APIs.
6. Sau khi tạo, mở lại **Inbound rules** và xác nhận không có rule SSH hoặc `0.0.0.0/0`
   cho port 443.

## 7. Tạo Security Group cho RDS

1. Tạo Security Group `aem-rds-sg` trong `aem-demo-vpc`.
2. Inbound duy nhất:
   - Type: PostgreSQL;
   - Port: 5432;
   - Source: Security Group `aem-ec2-sg` (không nhập CIDR).
3. Không cho phép `0.0.0.0/0`, public access hoặc nguồn là IP cá nhân.
4. Outbound giữ mặc định nếu chưa có yêu cầu khác.

## 8. Checklist hoàn thành O2

- [ ] VPC `aem-demo-vpc` có CIDR `10.20.0.0/16`.
- [ ] Internet Gateway đã attach.
- [ ] Có 1 public subnet cho EC2 và 2 private subnet ở 2 AZ cho RDS.
- [ ] Public route table có `0.0.0.0/0 → Internet Gateway`.
- [ ] Private route table chỉ có route local; không có NAT Gateway.
- [ ] `aem-ec2-sg` chỉ mở 443 từ CloudFront prefix list và 80 cho ACME.
- [ ] `aem-ec2-sg` không có inbound SSH.
- [ ] `aem-rds-sg` chỉ mở 5432 từ `aem-ec2-sg`.
- [ ] Đã kiểm tra prefix-list weight/quota trước khi sang O3.
- [ ] Ghi lại tên tài nguyên trong ghi chú riêng; không ghi ID/ARN thật vào GitHub.

Sau khi checklist đạt, gửi cho người phụ trách O3 ba thông tin: tên VPC, tên public subnet
và tên Security Group EC2. Không gửi access key, private key hoặc secret.

## 9. Tài liệu AWS tham khảo

- [Internet Gateway và public/private subnet](https://docs.aws.amazon.com/vpc/latest/userguide/VPC_Internet_Gateway.html)
- [AWS-managed prefix lists](https://docs.aws.amazon.com/vpc/latest/userguide/working-with-aws-managed-prefix-lists.html)
- [Security Group rules và quota](https://docs.aws.amazon.com/vpc/latest/userguide/security-group-rules.html)
