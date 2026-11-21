# Runbook triển khai AWS (Console-first)

Đây là tài liệu O1 cho việc dựng demo AI Exam Maker. Tài liệu mô tả thứ tự và cách kiểm tra
cho O2–O5. O1 không tự tạo tài nguyên AWS; dùng AWS Console để tạo tài nguyên và dùng các
file mẫu trong `/infra` để cấu hình máy.

## 1. Quyết định và nguyên tắc an toàn

- Region: `ap-southeast-1`.
- EC2: `t4g.small`, ARM64; API và Worker chạy chung máy.
- RDS: `db.t4g.micro`, Single-AZ, private subnet.
- EC2 ở public subnet; RDS ở private subnet.
- Không dùng NAT Gateway, ALB, Multi-AZ hoặc SSH ingress.
- Frontend: `app.<domain>` qua CloudFront; origin: `origin.<domain>` trỏ tới Elastic IP của EC2.
- TLS ở origin: Let's Encrypt HTTP-01. Port 80 chỉ phục vụ ACME challenge; port 443 chỉ nhận
  từ CloudFront managed prefix list.
- Chứng chỉ ACM của CloudFront phải được cấp ở `us-east-1`.

Không ghi secret, access key, private key, account ID, resource ID hoặc ARN vào GitHub. Dùng
Parameter Store cho cấu hình runtime và secret.

## 2. Thứ tự triển khai

### O2 — VPC, subnet và Security Group

Hướng dẫn thao tác Console và checklist chi tiết: [aws-o2-vpc-security-groups.md](aws-o2-vpc-security-groups.md).

1. Mở VPC Console ở `ap-southeast-1`, tạo VPC `aem-demo-vpc` với CIDR `10.20.0.0/16`.
2. Tạo Internet Gateway và attach vào VPC.
3. Tạo subnet ở ít nhất hai Availability Zone:
   - `aem-public-a` — `10.20.1.0/24` — public, dành cho EC2;
   - `aem-private-a` — `10.20.11.0/24` — private, dành cho RDS subnet group;
   - `aem-private-b` — `10.20.12.0/24` — private, ở AZ thứ hai, dành cho RDS subnet group.
4. Tạo route table `aem-public-rt`, associate với `aem-public-a`, thêm route:
   - `10.20.0.0/16` → `local`;
   - `0.0.0.0/0` → Internet Gateway.
5. Tạo route table `aem-private-rt`, associate với cả hai private subnet. Chỉ giữ route
   `10.20.0.0/16` → `local`; **không thêm NAT Gateway và không thêm Internet Gateway**.
6. Tạo Security Group `aem-ec2-sg` trong VPC:

   | Direction | Protocol/port | Source | Mục đích |
   |---|---|---|---|
   | Inbound | TCP 443 | `com.amazonaws.global.cloudfront.origin-facing` | CloudFront → Nginx |
   | Inbound | TCP 80 | `0.0.0.0/0` | Let's Encrypt HTTP-01 |
   | Inbound | TCP 22 | Không tạo rule | Không dùng SSH |
   | Outbound | All | `0.0.0.0/0` | Giữ mặc định để EC2 gọi AWS/API |

7. Tạo Security Group `aem-rds-sg` trong VPC:
   - Inbound TCP 5432: Source là **Security Group `aem-ec2-sg`**, không dùng CIDR và không
     dùng `0.0.0.0/0`.
   - Không tạo inbound khác.
8. Trước khi lưu rule 443, vào VPC → **Managed Prefix Lists** và kiểm tra AWS-managed prefix
   list có tên `com.amazonaws.global.cloudfront.origin-facing`. Prefix list CloudFront có
   weight 55, nên phải kiểm tra quota **Inbound or outbound rules per security group** trước
   khi thêm rule. Hai rule của EC2 dùng 56 inbound IPv4 slots: 55 cho CloudFront và 1 cho
   HTTP 80; quota mặc định 60 là đủ nếu Security Group không có rule khác.
9. Không cần chỉnh NACL cho O2; giữ NACL mặc định và kiểm tra không có rule deny bất thường.

### O3 — EC2, EBS, Elastic IP, SSM và IAM

Hướng dẫn thao tác Console và checklist chi tiết: [aws-o3-ec2-ssm.md](aws-o3-ec2-ssm.md).

1. Launch Amazon Linux 2023 ARM64 `t4g.small` trong `aem-public-a`, gắn `aem-ec2-sg`,
   root EBS `gp3` **30 GB**.
2. Gắn Elastic IP và ghi lại trong ghi chú riêng của team, không commit vào Git.
3. Gắn instance profile least-privilege cho SQS, Bedrock, SSM, Cognito Admin, S3 nếu thật
   sự cần, và CloudWatch logging nếu được phê duyệt. Không đặt access key trên EC2.
4. Chờ SSM Agent online rồi mở Session Manager. Không mở SSH.
5. Chạy `infra/bootstrap/user-data.sh.example` hoặc các lệnh tương đương qua SSM.
   Bootstrap cài Certbot trong `/opt/certbot` nhưng **không bật Nginx**.
6. API/Worker phải được publish self-contained cho ARM64, ví dụ:

   ```text
   dotnet publish -c Release -r linux-arm64 --self-contained true
   ```

7. Mỗi bản build đặt tại `/opt/exammaker/releases/<release-id>`, sau đó tạo symlink:

   ```text
   ln -sfn /opt/exammaker/releases/<release-id> /opt/exammaker/current
   ```

   Không tạo `/opt/exammaker/current` thành thư mục thật.
8. Chép systemd templates vào `/etc/systemd/system/`, tạo `api.env` và `worker.env` từ
   Parameter Store, rồi chạy `systemctl daemon-reload`. Chỉ enable/start service sau khi
   bản build và symlink đã tồn tại.

### O4 — Domain, DNS, Nginx và TLS

Hướng dẫn thao tác Console/SSM và checklist chi tiết: [aws-o4-domain-https.md](aws-o4-domain-https.md).

1. Tạo DNS record:
   - `origin.<domain>` → Elastic IP của EC2;
   - `app.<domain>` → CloudFront alias sau khi O5 tạo distribution.
2. Cài config HTTP-only trước khi xin chứng chỉ:
   - Xóa hoặc đổi tên `/etc/nginx/conf.d/default.conf` mặc định nếu nó tạo server block
     trùng port 80.
   - Chép `infra/nginx/acme-http.conf.example` vào `/etc/nginx/conf.d/` và thay domain.
   - Chạy `nginx -t`, sau đó `systemctl enable --now nginx`.
3. Tạo chứng chỉ origin bằng webroot:

   ```text
   certbot certonly --webroot -w /var/www/letsencrypt \
     -d origin.<domain> --email <team-email> --agree-tos --no-eff-email
   ```

   Chỉ chạy lệnh sau khi DNS `origin.<domain>` đã trỏ đúng Elastic IP và config HTTP-only
   đang hoạt động.
4. Tạo `/etc/exammaker/origin-secret.conf` từ Parameter Store, không lấy secret từ Git:
   - nội dung theo `infra/nginx/origin-secret.conf.example`;
   - owner `root:nginx`;
   - mode `0640`;
   - kiểm tra file không đọc được bởi user thường.
5. Sau khi chứng chỉ tồn tại, gỡ config HTTP-only và chép
   `infra/nginx/exammaker.conf.example` vào `/etc/nginx/conf.d/`. Thay domain, nhưng không
   đưa secret literal vào file này.
6. Cài deploy hook và timer gia hạn:
   - chép `infra/certbot/renewal-hooks/deploy/10-reload-nginx.sh.example` thành
     `/etc/letsencrypt/renewal-hooks/deploy/10-reload-nginx.sh`, owner `root:root`, mode `0750`;
   - chép `infra/systemd/certbot-renew.service.example` và
     `infra/systemd/certbot-renew.timer.example` vào `/etc/systemd/system/`;
   - chạy `systemctl daemon-reload && systemctl enable --now certbot-renew.timer`;
   - kiểm tra bằng `systemctl list-timers certbot-renew.timer` và
     `systemctl start certbot-renew.service`;
   - deploy hook chỉ chạy sau khi Certbot gia hạn thành công, chạy `nginx -t` rồi reload Nginx.
7. Chạy `nginx -t && systemctl reload nginx`. Kiểm tra request trực tiếp thiếu `X-Origin-Verify` nhận `403`. CloudFront sẽ gửi header
   secret và `CloudFront-Viewer-Address` ở O5; Nginx chuẩn hóa địa chỉ viewer thành một
   `X-Forwarded-For` duy nhất rồi xóa header secret trước khi proxy sang Kestrel để secret
   không lọt vào application log/exception dump.

### O5 — S3 và CloudFront

Chi tiết thao tác và checklist nằm ở [`aws-o5-s3-cloudfront.md`](aws-o5-s3-cloudfront.md).
Phần dưới đây là tóm tắt để giữ thứ tự triển khai trong runbook chung.

1. Tạo S3 bucket private trong region đã chọn. Bật Block Public Access và dùng CloudFront
   Origin Access Control (OAC), không dùng S3 website endpoint.
2. Upload static export của Next.js.
3. Tạo CloudFront distribution:
   - behavior `/_next/static/*` và `/assets/*` → S3, cache asset fingerprinted lâu;
   - behavior mặc định `/*` → S3, HTML cache ngắn và gắn CloudFront Function;
   - behavior `/api/*` → `origin.<domain>` qua HTTPS only;
   - `/api/*` dùng `CachingDisabled`;
   - `/api/*` dùng custom origin request policy: forward `CloudFront-Viewer-Address` nhưng
     không forward `Host`;
   - cho phép các HTTP method cần trong OpenAPI;
   - thêm custom origin header `X-Origin-Verify` cùng giá trị được lưu ở Parameter Store;
   - dùng ACM certificate ở `us-east-1`;
   - không dùng custom error 403/404 để rewrite thành `index.html`.
4. Gắn `infra/cloudfront-function/index-html-rewrite.js` vào viewer-request của behavior
   mặc định `/*` chỉ; không gắn vào `/api/*`.
5. Thêm alternate domain `app.<domain>` và trỏ DNS tới CloudFront.
6. Chờ distribution deployed, rồi kiểm tra frontend route, API route, request trực tiếp tới
   origin và refresh ở route static export.

## 3. Checklist kiểm tra

- [ ] `git diff --check` không có lỗi whitespace.
- [ ] Không có access key, private key, ID/ARN thật hoặc secret trong diff.
- [ ] Có 1 public subnet cho EC2 và 2 private subnet ở 2 AZ cho RDS subnet group.
- [ ] Không có NAT Gateway, ALB hoặc route private → Internet Gateway.
- [ ] EC2 là ARM64, EBS root là gp3 30 GB, binary là `linux-arm64` self-contained.
- [ ] SSM Session Manager hoạt động và không có inbound SSH.
- [ ] EC2 443 chỉ nhận CloudFront prefix list; port 80 chỉ phục vụ ACME challenge.
- [ ] Nginx reject request thiếu origin secret và không proxy secret sang Kestrel.
- [ ] File origin secret có owner `root:nginx`, mode `0640`.
- [ ] Hai service có `MemoryHigh`/`MemoryMax` và status systemd hoạt động.
- [ ] `certbot-renew.timer` được enable; deploy hook chạy `nginx -t` trước khi reload.
- [ ] CloudFront `/api/*` không cache và forward đủ header cần thiết.
- [ ] S3 chặn public access và chỉ nhận truy cập qua CloudFront OAC.
- [ ] AWS Budget được cấu hình bởi O14; dừng tài nguyên không dùng sau khi kiểm tra.

## 4. Dọn tài nguyên sau demo

Theo O16: tạo snapshot cần thiết, dừng RDS/EC2 khi không dùng, xóa EBS không còn gắn,
release Elastic IP không dùng, đồng thời kiểm tra không còn NAT Gateway, load balancer,
public IP idle hoặc CloudFront distribution thử nghiệm.

## 5. Tài liệu AWS tham khảo

- VPC public/private subnet và Internet Gateway: <https://docs.aws.amazon.com/vpc/latest/userguide/VPC_Internet_Gateway.html>
- AWS-managed prefix lists: <https://docs.aws.amazon.com/vpc/latest/userguide/working-with-aws-managed-prefix-lists.html>
- Security Group rules và quota: <https://docs.aws.amazon.com/vpc/latest/userguide/security-group-rules.html>
