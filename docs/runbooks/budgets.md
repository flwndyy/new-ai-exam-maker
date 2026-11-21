# Runbook — Thiết lập & Ứng phó AWS Budgets $200 (Task O14)

Tài liệu hướng dẫn dựng, kiểm tra và quy trình ứng phó cảnh báo chi phí cho ngân sách **$200 AWS credit** của dự án **AI Exam Maker** theo `docs/context.md` (mục 6).

> **Quy tắc bảo mật:** Tuyệt đối **không** commit địa chỉ email cá nhân của thành viên hay AWS Account ID vào repository. Danh sách email luôn được truyền qua biến môi trường `BUDGET_EMAILS` lúc chạy script.

---

## 1. Thông số chuẩn & Lý do thiết kế

| Thành phần | Giá trị chuẩn | Lý do thiết kế |
|---|---|---|
| Tên Budget | `exammaker-200usd-budget` | Chuẩn đặt tên dự án |
| Loại Budget | `COST` (chỉ gửi cảnh báo, không gắn Budget Action) | Chi phí **$0.00/tháng** (notification-only Cost Budget miễn phí trên AWS) |
| Hạn mức | **`$200` USD** | Khớp trần $200 toàn dự án (`docs/context.md` §6) |
| Chu kỳ (`TimeUnit`) | **`QUARTERLY`** (3 tháng) | Bao trọn **toàn bộ 2 tháng dev + demo** trong một hạn mức duy nhất $200 (tránh `MONTHLY` tự reset về $0 sang tháng mới làm mất theo dõi tổng) |
| Theo dõi Credit | **`IncludeCredit = false`, `IncludeRefund = false`** | **Rất quan trọng:** nếu để `IncludeCredit = true`, AWS trừ sẵn $200 credit nên chi tiêu thực tế luôn hiển thị `$0.00` cho tới khi cháy sạch credit, khiến cảnh báo 50%/80% không bao giờ kích hoạt! Tắt `IncludeCredit` giúp theo dõi chính xác tốc độ tiêu thụ $200 credit. |
| Các mốc cảnh báo | **50% ($100)**, **80% ($160)**, **100% ($200)** `ACTUAL` + **80% ($160)** `FORECASTED` | Cảnh báo sớm qua email cho cả 4 thành viên (`DEV-A`, `DEV-B`, `OPS-1`, `OPS-2`) |

---

## 2. Cách 1: Dựng tự động bằng AWS CLI script (Khuyên dùng)

**Yêu cầu công cụ:** `aws` (CLI v2), `jq` — cài sẵn trong **AWS CloudShell**.

```bash
BUDGET_EMAILS="devA@example.com,devB@example.com,ops1@example.com,ops2@example.com" \
  ./infra/budgets/setup-budgets.sh
```

**Hành vi an toàn của script:**
- Tự động lấy 12 chữ số Account ID từ phiên đăng nhập hiện tại (`aws sts get-caller-identity`).
- Kiểm tra định dạng và loại trùng email; **không in địa chỉ email hay Account ID** ra màn hình.
- Mặc định **create-if-absent** (nếu budget đã tồn tại sẽ giữ nguyên `[KEPT]`). Muốn cập nhật hạn mức hoặc bổ sung thêm email thành viên, chạy kèm `UPDATE_BUDGET=1`:

```bash
UPDATE_BUDGET=1 \
BUDGET_EMAILS="devA@example.com,devB@example.com,ops1@example.com,ops2@example.com" \
  ./infra/budgets/setup-budgets.sh
```

---

## 3. Cách 2: Dựng thủ công trên AWS Console

1. Vào **AWS Billing and Cost Management → Budgets → Create a budget**.
2. Chọn **Customize (advanced)** → **Cost budget**.
3. Đặt tên `exammaker-200usd-budget`:
   - Period: **Quarterly** · Renewal type: **Recurring budget**.
   - Budgeted amount: **Fixed** → **`200`** USD.
   - **Advanced options → Aggregate costs by:** chọn **Unblended costs**, **BỎ CHỌN** *Credits* và *Refunds*.
4. Cấu hình **4 Alert thresholds** (nhập email cả 4 thành viên vào mục *Email recipients*):
   - Alert #1: **50%** of budgeted amount (**Actual**).
   - Alert #2: **80%** of budgeted amount (**Actual**).
   - Alert #3: **100%** of budgeted amount (**Actual**).
   - Alert #4: **80%** of budgeted amount (**Forecasted**).
5. Bấm **Create budget**.

---

## 4. Xác minh tự động (Verification)

```bash
# Kiểm tra 10 tiêu chí (mặc định kiểm tra >= 1 email subscriber)
./infra/budgets/verify-budgets.sh

# Kiểm tra chặt đã đủ cả 4 thành viên trong nhóm
MIN_SUBSCRIBERS=4 ./infra/budgets/verify-budgets.sh
```

Script kiểm tra đầy đủ **10 tiêu chí** (không in email hay Account ID):
1. Budget `exammaker-200usd-budget` tồn tại.
2. `BudgetType == COST`.
3. `BudgetLimit <= $200 USD`.
4. `TimeUnit == QUARTERLY`.
5. Đã loại trừ `Credit` và `Refund` (`IncludeCredit = false`, `IncludeRefund = false`).
6. Có ngưỡng `ACTUAL > 50%` kèm `EMAIL` subscriber.
7. Có ngưỡng `ACTUAL > 80%` kèm `EMAIL` subscriber.
8. Có ngưỡng `ACTUAL > 100%` kèm `EMAIL` subscriber.
9. Có ngưỡng `FORECASTED > 80%` kèm `EMAIL` subscriber.
10. Đọc được số liệu `CalculatedSpend` (`Actual` / `Forecasted`).

---

## 5. Playbook ứng phó khi nhận email cảnh báo

> **Lưu ý quan trọng (`docs/context.md` §6):** AWS Budgets **chỉ gửi email cảnh báo, không tự ngắt dịch vụ**. Khi nhận email cảnh báo, OPS-2 và cả nhóm thực hiện theo từng mốc sau:

### Mốc 50% ($100) — Kiểm tra định kỳ
1. Mở **AWS Cost Explorer** (nhóm theo *Service*, lọc loại trừ *Credit*): đối chiếu với dự toán `docs/context.md` §6 (đặc biệt kiểm tra **Amazon Bedrock**, **EC2**, **RDS**, **Public IPv4 / EIP**).
2. Xác nhận không có tài nguyên ngoài kiến trúc (NAT Gateway, ALB, EBS volume rời, manual snapshot cũ, KMS CMK thừa).
3. Kiểm tra lịch tắt EC2 & RDS ban đêm (23:00–07:00) có đang được duy trì không.

### Mốc 80% ($160) hoặc 80% Forecasted — Siết chi phí ngay
1. **Chuyển Bedrock về model rẻ (Haiku) và tắt kiểm chứng đáp án** trên Parameter Store:
   ```bash
   aws ssm put-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/ModelId --type String --value "apac.anthropic.claude-3-5-haiku-20241022-v1:0" --overwrite
   aws ssm put-parameter --region ap-southeast-1 --name /exammaker/dev/Bedrock/EnableAnswerVerification --type String --value "false" --overwrite
   ```
2. **Hạ quota AI tạm thời** trên Parameter Store (ví dụ `SystemTokensPerDay=150000`, `JobsPerUserPerDay=5`) và restart API/Worker trên EC2.
3. Rà soát CloudWatch Logs (đảm bảo retention 7 ngày, không bị vòng lặp log lỗi).

### Mốc 100% ($200) — Dừng khẩn cấp tài nguyên tính phí theo giờ
1. Dừng EC2 (`aem-exammaker-ec2`) và RDS (`exammaker-dev-pg`) ngay khi không demo *(nhắc: RDS tự khởi động lại sau 7 ngày dừng — cần theo dõi theo O16)*.
2. Hạ `AiQuota/SystemTokensPerDay` xuống `1` hoặc tắt Worker để chặn phát sinh thêm phí Bedrock.
3. Thông báo ngay cho cả nhóm để kiểm tra nguyên nhân trước khi bật lại cho buổi demo chính thức.
