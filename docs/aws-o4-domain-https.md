# O4 — Domain, DNS, Nginx và HTTPS

Tài liệu này hướng dẫn cấu hình domain cho frontend/origin, chứng chỉ ACM cho CloudFront,
Let's Encrypt cho origin EC2 và Nginx reverse proxy. Tài liệu không chứa domain, IP, ARN
hoặc secret thật; các giá trị `<domain>` và `<team-email>` chỉ là placeholder.

## 1. Điều kiện trước khi làm

- O2 đã hoàn tất trong Region `ap-southeast-1`.
- O3 đã tạo EC2, Elastic IP và SSM Session Manager.
- Bootstrap O1 đã chạy qua SSM; Nginx vẫn có thể đang stopped.
- Có domain mà team có quyền tạo DNS record.
- Có Parameter Store path/secret theo quy ước O12. Không gửi origin secret qua chat công khai.

Quy ước hostname:

```text
origin.<domain> → Elastic IP của EC2
app.<domain>    → CloudFront distribution (sau khi O5 tạo)
```

## 2. DNS cho origin

Tại Route 53 hoặc nhà cung cấp DNS:

1. Tạo bản ghi `A` cho `origin.<domain>` trỏ tới Elastic IP của EC2.
2. Không tạo bản ghi `app.<domain>` trỏ thẳng tới EC2; hostname này sẽ trỏ tới CloudFront ở O5.
3. Kiểm tra DNS đã phân giải tới đúng Elastic IP trước khi xin Let's Encrypt.

Từ máy có công cụ DNS, kiểm tra:

```text
nslookup origin.<domain>
```

Không commit địa chỉ IP vào repository.

## 3. Chứng chỉ ACM cho CloudFront

ACM của CloudFront bắt buộc ở `us-east-1`, khác với các tài nguyên EC2/RDS ở Singapore.

1. Chuyển AWS Console sang **US East (N. Virginia) — `us-east-1`**.
2. Vào **Certificate Manager → Request**.
3. Chọn **Request a public certificate**.
4. Thêm domain `app.<domain>`; nếu team cần wildcard thì thêm `*.<domain>` sau khi đã thống nhất.
5. Chọn **DNS validation**.
6. Tạo bản ghi CNAME validation tại DNS provider.
7. Chờ trạng thái chứng chỉ là **Issued**.
8. Ghi lại ARN ở ghi chú riêng để O5 chọn; không commit ARN vào Git.

Chứng chỉ ACM này dùng cho viewer CloudFront. Không dùng nó cho Nginx origin.

## 4. Cài cấu hình HTTP-only cho ACME

Mở **Systems Manager → Session Manager → Start session** vào EC2, sau đó chép các file
O1 lên máy theo quy trình deploy của team. Trong shell SSM:

1. Xóa hoặc đổi tên `/etc/nginx/conf.d/default.conf` nếu nó tạo server block port 80 trùng.
2. Chép `infra/nginx/acme-http.conf.example` thành file trong `/etc/nginx/conf.d/`.
3. Thay `origin.example.invalid` bằng `origin.<domain>`.
4. Kiểm tra và bật Nginx:

   ```bash
   sudo nginx -t
   sudo systemctl enable --now nginx
   ```

5. Kiểm tra URL challenge trả về đường dẫn dưới `/var/www/letsencrypt`; các request HTTP
   khác phải trả `404`. Không bật HTTPS config trước khi chứng chỉ tồn tại.

## 5. Xin Let's Encrypt cho origin

Chỉ chạy sau khi `origin.<domain>` đã trỏ đúng Elastic IP và port 80 phục vụ challenge:

```bash
sudo certbot certonly --webroot -w /var/www/letsencrypt \
  -d origin.<domain> --email <team-email> --agree-tos --no-eff-email
```

Kiểm tra hai file sau tồn tại:

```text
/etc/letsencrypt/live/origin.<domain>/fullchain.pem
/etc/letsencrypt/live/origin.<domain>/privkey.pem
```

## 6. Cấu hình secret và Nginx HTTPS

1. Lấy origin secret từ Parameter Store theo quy ước O12; không ghi secret vào Git hoặc
   gửi qua chat.
2. Tạo `/etc/exammaker/origin-secret.conf` từ
   `infra/nginx/origin-secret.conf.example`.
3. Thay placeholder bằng secret trên EC2, sau đó đặt quyền:

   ```bash
   sudo chown root:nginx /etc/exammaker/origin-secret.conf
   sudo chmod 0640 /etc/exammaker/origin-secret.conf
   ```

4. Chép `infra/nginx/exammaker.conf.example` vào `/etc/nginx/conf.d/`.
5. Thay `origin.example.invalid` bằng `origin.<domain>` ở `server_name` và đường dẫn
   chứng chỉ; không thay secret literal trong file Nginx.
6. Gỡ hoặc đổi tên config HTTP-only, rồi kiểm tra:

   ```bash
   sudo nginx -t
   sudo systemctl reload nginx
   ```

Nginx chỉ proxy tới `127.0.0.1:5000`, chỉ chấp nhận header origin secret ở HTTPS và xóa
`X-Origin-Verify` trước khi chuyển request tới Kestrel.

## 7. Timer gia hạn chứng chỉ

1. Cài deploy hook:

   ```bash
   sudo install -o root -g root -m 0750 \
     infra/certbot/renewal-hooks/deploy/10-reload-nginx.sh.example \
     /etc/letsencrypt/renewal-hooks/deploy/10-reload-nginx.sh
   ```

2. Cài hai unit:

   ```bash
   sudo install -o root -g root -m 0644 infra/systemd/certbot-renew.service.example \
     /etc/systemd/system/certbot-renew.service
   sudo install -o root -g root -m 0644 infra/systemd/certbot-renew.timer.example \
     /etc/systemd/system/certbot-renew.timer
   sudo systemctl daemon-reload
   sudo systemctl enable --now certbot-renew.timer
   ```

3. Kiểm tra timer:

   ```bash
   systemctl list-timers certbot-renew.timer
   systemctl status certbot-renew.timer --no-pager
   ```

Certbot chỉ chạy deploy hook sau khi gia hạn thành công; hook kiểm tra `nginx -t` rồi reload.
Không dùng `certbot renew --force-renewal` trong demo vì có thể chạm giới hạn Let's Encrypt.

## 8. Kiểm tra forwarded headers và origin guard

API đã bật `ForwardedHeaders` và chỉ tin proxy loopback `127.0.0.1`. Nginx đặt
`X-Forwarded-For`/`X-Forwarded-Proto` và xóa `X-Origin-Verify` trước Kestrel.

Kiểm tra trên EC2, không ghi secret vào shell history hoặc log:

```bash
sudo nginx -t
curl --resolve origin.<domain>:443:127.0.0.1 -sk -o /dev/null -w '%{http_code}\n' \
  https://origin.<domain>/health
```

Request HTTPS thiếu origin header phải trả `403`. Header đúng chỉ được gửi bởi CloudFront
ở O5; không mở port 443 rộng ra Internet để test.

## 9. Checklist O4

- [ ] `origin.<domain>` phân giải tới Elastic IP của EC2.
- [ ] ACM public certificate cho `app.<domain>` ở `us-east-1` là **Issued**.
- [ ] Nginx HTTP-only đã phục vụ ACME trước khi xin chứng chỉ.
- [ ] Let's Encrypt origin certificate tồn tại trên EC2.
- [ ] Nginx HTTPS config dùng đúng domain và secret include root-only `0640`.
- [ ] Port 80 chỉ phục vụ ACME/404; port 443 vẫn giới hạn bởi Security Group CloudFront.
- [ ] `certbot-renew.timer` enabled và active.
- [ ] Deploy hook chạy `nginx -t` rồi reload sau khi renew thành công.
- [ ] Request origin thiếu `X-Origin-Verify` nhận `403`.
- [ ] API đã chạy `UseForwardedHeaders` trước request logging/authentication.
- [ ] Không có domain, IP, ARN, private key hoặc secret thật trong Git.
