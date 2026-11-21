# O5 — S3 private, OAC và CloudFront

Tài liệu này triển khai frontend Next.js static export qua S3 private và CloudFront, đồng
thời định tuyến `/api/*` tới Nginx trên EC2. Các giá trị `<domain>`, `<bucket-name>`,
`<distribution-id>`, ARN và secret chỉ là placeholder; thay trong AWS Console, không commit
giá trị thật vào repository.

## 1. Điều kiện trước khi làm

- O2 đã giới hạn Security Group EC2: `443` chỉ từ CloudFront managed prefix list, `80` chỉ
  cho HTTP-01/ACME; không mở SSH.
- O3 đã có EC2, Elastic IP và SSM Session Manager.
- O4 đã có `origin.<domain>` trỏ tới Elastic IP, Nginx phục vụ HTTPS và chứng chỉ ACM cho
  `app.<domain>` ở `us-east-1` đã ở trạng thái **Issued**.
- Parameter Store có origin secret theo path của môi trường. Không dán secret vào file,
  chat hoặc commit.
- Frontend dùng `output: "export"` và `trailingSlash: true`.

## 2. Tạo S3 bucket private

Thực hiện trong Region deploy frontend (theo dự án là `ap-southeast-1`):

1. Mở **S3 → Create bucket** và đặt tên duy nhất `<bucket-name>`.
2. Giữ **Block all public access** bật; không bật S3 static website hosting.
3. Giữ **Object Ownership: Bucket owner enforced** và bật mã hóa mặc định SSE-S3.
4. Không tạo access key riêng cho bucket. CloudFront sẽ đọc bằng OAC.

Sau khi có distribution và biết ARN của distribution, thêm bucket policy chỉ cho phép
CloudFront service principal đọc object của distribution đó. Mẫu policy cần thay placeholder:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "AllowCloudFrontServicePrincipalReadOnly",
      "Effect": "Allow",
      "Principal": { "Service": "cloudfront.amazonaws.com" },
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::<bucket-name>/*",
      "Condition": {
        "StringEquals": {
          "AWS:SourceArn": "arn:aws:cloudfront::<account-id>:distribution/<distribution-id>"
        }
      }
    }
  ]
}
```

Không dùng `Principal: "*"` và không thêm `s3:ListBucket` nếu distribution không cần.

## 3. Tạo CloudFront distribution và OAC

1. Mở **CloudFront → Create distribution**.
2. Chọn S3 REST endpoint của bucket `<bucket-name>` làm origin; không chọn website endpoint.
3. Tạo **Origin access control** mới với **Sign requests** và **SigV4**.
4. Default root object để trống hoặc `index.html`; Function bên dưới chịu trách nhiệm rewrite
   các route directory.
5. Chọn ACM certificate cho `app.<domain>` ở **US East (N. Virginia) — `us-east-1`**.
6. Thêm alternate domain name `app.<domain>`, bật HTTP → HTTPS redirect và dùng TLS policy
   hiện hành phù hợp.

## 4. Behavior frontend và cache

Tạo các behavior theo thứ tự ưu tiên sau; behavior mặc định nằm cuối danh sách:

1. `/api/*` — theo mục 5.
2. `/_next/static/*` — asset Next.js đã fingerprint.
3. `/assets/*` — asset tĩnh có fingerprint nếu dự án tạo thư mục này.
4. `/*` — HTML và các route static export.

Với hai behavior asset (`/_next/static/*` và `/assets/*`):

- Origin: S3 `<bucket-name>` qua OAC.
- Viewer protocol: **Redirect HTTP to HTTPS**.
- Allowed methods: `GET, HEAD, OPTIONS`.
- Cache policy custom `StaticAssetsLong`: minimum/default/max TTL lần lượt là `0`,
  `31536000`, `31536000` giây; bật compression.
- Không gắn CloudFront Function.

Với behavior mặc định `/*`:

- Origin: S3 `<bucket-name>` qua OAC.
- Viewer protocol: **Redirect HTTP to HTTPS**.
- Allowed methods: `GET, HEAD, OPTIONS`.
- Cache policy custom `HtmlShort`: minimum/default/max TTL lần lượt là `0`, `60`, `300`
  giây; không cache cookie không cần thiết.
- Gắn `infra/cloudfront-function/index-html-rewrite.js` ở **Viewer request**.
- Không gắn Function này vào `/api/*` hoặc hai behavior asset.

Function biến `/` thành `/index.html`, route không có phần mở rộng thành
`/route/index.html`, còn asset và API giữ nguyên. Không cấu hình custom error response
`403/404 → index.html`; cách đó có thể che lỗi API và làm security boundary khó kiểm tra.

Upload bản build static từ thư mục `frontend/out/` sau khi chạy build:

```bash
cd frontend
npm ci
npm run build
# HTML và file chưa fingerprint: cache ngắn để deploy có hiệu lực nhanh.
aws s3 sync out/ s3://<bucket-name>/ --delete \
  --exclude "_next/static/*" \
  --exclude "assets/*" \
  --cache-control "public,max-age=60,must-revalidate"
# Asset fingerprinted: cache lâu, immutable.
aws s3 cp out/ s3://<bucket-name>/ --recursive \
  --exclude "*" \
  --include "_next/static/*" \
  --include "assets/*" \
  --cache-control "public,max-age=31536000,immutable"
aws cloudfront create-invalidation \
  --distribution-id <distribution-id> \
  --paths "/*"
```

Chỉ chạy `--delete` khi đã kiểm tra đúng bucket frontend của môi trường. Lượt `sync` đầu
không đụng vào asset fingerprinted; lượt `cp --recursive` luôn ghi lại metadata của nhóm
asset để bảo đảm `Cache-Control: immutable`, kể cả khi nội dung asset không đổi. Invalidation
vẫn được chạy sau deploy để xóa object cũ còn nằm trong edge cache.

## 5. Behavior `/api/*` tới origin EC2

Tạo behavior mới và đặt **trên** behavior mặc định:

- Path pattern: `/api/*`.
- Origin: custom origin `origin.<domain>`.
- Origin protocol policy: **HTTPS only**, port `443`; chứng chỉ origin phải khớp hostname.
- Viewer protocol: **Redirect HTTP to HTTPS**.
- Allowed methods: `GET, HEAD, OPTIONS, PUT, POST, PATCH, DELETE`.
- Cache policy: **CachingDisabled**.
- Origin request policy: tạo **custom origin request policy**; không forward `Host` của viewer,
  nhưng bắt buộc forward `CloudFront-Viewer-Address` cùng các header/query cần cho API.
- Không dùng CloudFront Function ở behavior này.

### Header và origin secret

Trong phần **Origin → Custom headers**, thêm:

```text
Header name:  X-Origin-Verify
Header value: <origin-secret-from-parameter-store>
```

`<origin-secret-from-parameter-store>` phải được đọc từ Parameter Store và dán trực tiếp vào
AWS Console; không ghi vào repository. Nginx kiểm tra header này rồi xóa nó trước khi proxy
sang Kestrel. CloudFront phải chuyển tiếp `CloudFront-Viewer-Address` để O4 chuẩn hóa IP
người dùng.

Custom origin request policy cần có các thành phần sau:

- Query strings: **All**.
- Headers: `Authorization`, `Content-Type`, `Accept`, `Origin`, các header CORS cần dùng và
  `CloudFront-Viewer-Address`; không chọn `Host`.
- Cookies: chỉ các cookie API thực sự cần, mặc định không forward.

## 6. DNS và kiểm tra sau triển khai

Tại DNS provider:

```text
app.<domain>    CNAME/ALIAS → <distribution-domain-name>
origin.<domain> A          → Elastic IP của EC2 (đã tạo ở O4)
```

Chờ distribution chuyển sang **Deployed**, sau đó kiểm tra:

1. `https://app.<domain>/` trả frontend với mã `200`.
2. Refresh một deep link, ví dụ `https://app.<domain>/dashboard/`, vẫn trả đúng trang.
3. Asset `/assets/...js` không bị đổi thành `index.html`.
4. Gọi endpoint API qua `https://app.<domain>/api/...`: request tới đúng EC2, không bị cache
   và response không chứa origin secret.
5. Request trực tiếp tới `https://origin.<domain>/...` thiếu `X-Origin-Verify` nhận `403`.
6. Truy cập public S3 object URL không được phép; chỉ CloudFront đọc được object.
7. HTTP viewer được redirect sang HTTPS.

Kiểm tra Function cục bộ:

```bash
node --test infra/cloudfront-function/index-html-rewrite.test.mjs
```

## 7. Checklist O5

- [ ] S3 bucket private, Block Public Access bật, không dùng website endpoint.
- [ ] OAC dùng SigV4 và bucket policy giới hạn đúng CloudFront distribution.
- [ ] ACM viewer certificate ở `us-east-1`, alternate domain là `app.<domain>`.
- [ ] Behavior `/api/*` đứng trước `/*`, dùng HTTPS only và `CachingDisabled`.
- [ ] `/api/*` cho đủ method, không forward `Host`, có `CloudFront-Viewer-Address`.
- [ ] `X-Origin-Verify` được cấu hình ở origin và không xuất hiện trong log/response ứng dụng.
- [ ] Behavior `/*` dùng S3 + Function viewer-request; không có custom error SPA fallback.
- [ ] DNS `app.<domain>` trỏ CloudFront và `origin.<domain>` vẫn trỏ Elastic IP.
- [ ] Đã kiểm tra root, deep link, asset, API, direct origin và S3 public access.
- [ ] Không có domain thật, IP, account ID, ARN, private key hoặc secret trong Git.
