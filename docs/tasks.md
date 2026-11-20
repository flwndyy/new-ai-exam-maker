# Phân công công việc

Dựa trên `docs/context.md` v0.3. Đây là **đề xuất ban đầu**: cả nhóm xem lại, đổi người nếu cần rồi cập nhật file này. Khi xong một việc thì tick `[x]` trong PR của việc đó.

| Mã | Người | Mảng | Phạm vi |
|---|---|---|---|
| **DEV-A** | @Lancelot-sys25 | Backend lõi + quy trình duyệt | Nền backend, auth, user, danh mục, vòng đời câu hỏi và đề, audit; màn hình admin, ngân hàng câu hỏi, duyệt |
| **DEV-B** | @flwndyy | AI + soạn đề + xuất đề | Khung frontend, đăng nhập, pipeline AI (SQS/worker/Bedrock), ghép đề, xuất đề |
| **OPS-1** | @HuuPhuoc-NH | Mạng, edge, compute | VPC/SG, EC2, Nginx + HTTPS, domain, CloudFront, S3, OIDC, CD frontend |
| **OPS-2** | @TranGiaBao2005 | Dữ liệu, danh tính, giám sát, chi phí | Cognito, RDS, SQS, Parameter Store, CloudWatch, Budgets, CD backend |

Mỗi dev sở hữu **cả backend và frontend** của phần mình để khỏi chờ nhau. Thư mục `/backend` và `/frontend` chung, nên **không đụng file của nhau khi chưa báo** (xem "Ranh giới" cuối file).

---

## Lịch 2 tuần (17/11 → hạn chót 01/12/2026)

Chia 4 chặng. Mốc cuối mỗi chặng là ngày **phải xong** để chặng sau không bị chặn. Cuối tuần tính là ngày làm nhẹ.

| Chặng | Ngày | Mục tiêu | Mốc "xong" |
|---|---|---|---|
| **0** | 17–19/11 | Chốt nền | ERD + OpenAPI duyệt; repo bảo vệ; chốt điểm AWS còn lại; Cognito/RDS/SQS bắt đầu |
| **1** | 20–24/11 | Nền tảng | Backend chạy được, đăng nhập thật qua CloudFront, DB + migration + seed, EC2/CloudFront/Cognito/RDS/SQS sẵn sàng |
| **2** | 25–29/11 | Tính năng | Luồng demo (mục 2.1) chạy đủ đầu-cuối ở môi trường thật; CD frontend/backend hoạt động |
| **3** | 30/11–01/12 | Hoàn thiện & demo | E2E, sửa lỗi, thử khôi phục RDS, kiểm tra ngân sách, chuẩn bị demo. **Không thêm tính năng mới.** |

### Việc của từng người theo chặng

| Người | Chặng 0 (17–19/11) | Chặng 1 (20–24/11) | Chặng 2 (25–29/11) | Chặng 3 (30/11–01/12) |
|---|---|---|---|---|
| **DEV-A** @Lancelot-sys25 | **0.2** ERD → bắt đầu A1 | **A1, A2, A3, A5, A4** | **A6, A7, A8, A9, A10** | Sửa lỗi, hỗ trợ E2E |
| **DEV-B** @flwndyy | **0.1** GitHub, **0.3** OpenAPI | **B1, B2, B3** | **B5, B6, B7, B8, B9, B10** | **B11** E2E, **B4** (nếu kịp) |
| **OPS-1** @HuuPhuoc-NH | **0.5** (chung OPS-2), bắt đầu O2 | **O1, O2, O3, O4, O5** | **O6, O7, O8** | Hỗ trợ demo, kiểm tra CloudFront/SG |
| **OPS-2** @TranGiaBao2005 | **0.4**, **0.6**, bắt đầu **O9** | **O9** (xong sớm nhất), **O10, O11, O12** | **O13, O14, O15** | **O16**, thử khôi phục RDS |

Quy tắc chặn nhau (ưu tiên làm sớm):
- **O9 Cognito xong trước 21/11** → mở khóa A4, B2.
- **0.2 + 0.3 xong trước 19/11** → mở khóa A2, B1, B5, B9.
- **O10 + O11 + O12 xong trước 24/11** → mở khóa B5, B6 trên môi trường thật.
- **A7 xong trước 27/11** → B9, B10 mới ghép được thật.

### Phạm vi cho 2 tuần (cắt nếu trễ)

43 việc trong 2 tuần cho 4 người là rất chặt. Nếu trễ ở cuối chặng 1 hoặc chặng 2, **cắt theo thứ tự này** (luồng demo chính mục 2.1 vẫn phải chạy):

| Việc | Bản rút gọn dùng cho 2 tuần | Phần để sau |
|---|---|---|
| B4 | Cắt hẳn | Kiểm chứng đáp án, loại trùng |
| A4 | Chỉ: script tạo admin đầu tiên + tạo/vô hiệu user qua API | `reconcile` |
| A10 | Chỉ: ngân hàng câu hỏi + màn duyệt câu/đề | Màn admin quản lý user/tổ/môn/chương (dùng seed + API) |
| O1 | Dựng tay qua Console **và ghi lại runbook** trong `docs/` | IaC |
| O13 | 3 alarm bắt buộc: DLQ depth, 5xx, heartbeat worker | Các alarm còn lại |
| B7 | Test idempotency (trùng message) và retry/DLQ | Các ca còn lại |
| A9 | Test state machine + ma trận quyền cho luồng demo | Phần còn lại |
| B11 | Checklist chạy tay theo mục 2.1 | E2E tự động |
| Xuất đề | PDF qua trình duyệt (đã chốt) | DOCX |

---

## Chặng 0 — Chốt nền (17–19/11, làm trước, song song)

| ID | Việc | Người | Tham chiếu | Xong khi |
|---|---|---|---|---|
| 0.1 | Cấu hình GitHub: ruleset `main` (chỉ Merge commit) + `dev` (chặn xóa/force push) + tag, Squash cho PR vào `dev`, tự xóa nhánh ngắn, Actions read-only, Secret scanning + Push protection + Dependabot; bật 2FA cả 4 người; mời thành viên (2 Admin, 2 Write); bật "Require review from Code Owners" | DEV-B (chủ repo) + OPS-2 (Admin dự phòng) | B1–B7, A7 | Không push thẳng được vào `main` |
| 0.2 | **ERD** đầy đủ (mục 8, gồm `payload`, snapshot, job, audit) | DEV-A soạn, DEV-B review | C7, mục 8 | `docs/erd.md` được cả 2 dev duyệt |
| 0.3 | **API contract** OpenAPI (mục 9.1 + lỗi `problem+json`, phân trang) | DEV-B soạn, DEV-A review | C7, mục 9 | `docs/api/openapi.yaml` — **frontend và backend cùng bám file này** |
| 0.4 | Bảng ngân sách: điền mục 6 bằng AWS Pricing Calculator; DEV-B cung cấp ước lượng token/lần sinh | OPS-2 (+ DEV-B) | C5, mục 6 | Tổng ≤ $200, có dự phòng; chốt mức dự phòng |
| 0.5 | Chốt 5 điểm AWS còn lại ở mục 14 (domain, Bedrock model/region/quota, t3/t4g, IaC, dự phòng) | OPS-1 + OPS-2, DEV-B tham gia phần Bedrock | mục 14 | Chuyển sang "Quyết định đã chốt" |
| 0.6 | Thêm workflow CI cơ bản (lint + build + test, gitleaks) làm status check cho `main` | OPS-2 | B1, B7 | CI xanh trên PR mẫu |

> Việc phụ thuộc: **0.2 và 0.3 chặn hầu hết chặng 1 và 2**. Chốt xong 0.4 + 0.5 thì mới giao AI agent viết code lớn (checklist mục 13).

---

## DEV-A — Backend lõi + quy trình duyệt (@Lancelot-sys25)

**Chặng 1 (20–24/11) — Nền (chặn DEV-B)**
- [ ] A1. Solution .NET 10 theo mục 7: `Api / Application / Domain / Infrastructure / Worker` + `tests`; Serilog; `ProblemDetails`; health endpoint; cấu hình đọc từ Parameter Store.
- [x] A2. EF Core + Npgsql, migration đầu (toàn bộ bảng mục 8), unique/partial index, CHECK, FK `RESTRICT`; **seed** `departments/subjects/topics` (mục 8.4).
  - Kiểm tra local 19/11/2026: đủ 13 bảng; seed 4 tổ, 4 môn, 82 chương; 30 test pass (13 integration test PostgreSQL trong repo, không skip); migration từ DB mới, nâng cấp DB hiện có, rollback/reapply và SQL idempotent đều pass. PR #55 đã merge vào dev, CI commit cuối f6c1e23 xanh; issue #8 đã đóng (Completed).
- [ ] A3. Auth: validate JWT (issuer/audience/hạn) → tải user từ DB theo `sub` (cache 1–5 phút) → policy theo role + tổ + ownership; `GET /me`. Từ chối `sub` chưa có trong `users`.
  - Kiểm tra local 19/11/2026: code JWT Cognito, lookup DB + cache hiển thị 1 phút, policy role/tổ/ownership và GET /me; 74 test Release pass, không skip (15 test PostgreSQL). Comment kỹ thuật bằng tiếng Anh; tài liệu giải thích tiếng Việt. Smoke test Cognito thật đã đạt ngày 20/11/2026; xem docs/a3-auth.md. Chờ PR merge vào dev và CI xanh trước khi tick hoàn tất.
  - Kiểm tra sửa review 20/11/2026: 61 test API Release pass (gồm SelfReview qua HTTP và quy tắc Application), 16 test model pass; 15 test PostgreSQL skip ở lượt local do Docker chưa chạy. Sau đó CI trên commit ea56350 đã xanh: 92/92 test pass, không skip (61 API, 31 Infrastructure, gồm 15 PostgreSQL). Lượt skip chỉ là kết quả local trước đó.
  - Smoke test Cognito thật 20/11/2026 do DEV-A thực hiện: Authorization Code + PKCE thành công; GET /api/v1/me trả 200 và hồ sơ teacher đúng tổ. Khóa user trong PostgreSQL local: cùng access token nhận 401; mở khóa: cùng token nhận lại 200. Không lưu token, thông tin đăng nhập hoặc định danh tài nguyên thật trong repo.

- [ ] A4. Quản lý user + đồng bộ Cognito (`AdminCreateUser`/`AddUserToGroup`/`Disable`/`GlobalSignOut`, đền bù khi lỗi); `POST /admin/users/reconcile`; **script tạo admin đầu tiên** (mục 3.3). *Cần Cognito của OPS-2.*
  - Triển khai local 20/11/2026: API danh sách/chi tiết/tạo/sửa/xóa mềm user chỉ cho admin; đồng bộ Cognito có đền bù, fail-closed, audit log, đối soát chỉ đọc và CLI bootstrap admin. Migration AddUserAdministration bổ sung created_at và unique email chuẩn hóa.
  - Kiểm tra Release: 128/128 test pass, không skip (78 API, 50 Infrastructure; gồm 28 test PostgreSQL thật và 6 test adapter AWS dùng stub). Chưa chạy bootstrap/tạo lời mời hoặc smoke test A4 với Cognito thật. Chờ review, CI, kiểm thử AWS thật và PR merge vào dev trước khi tick hoàn tất. Xem docs/a4-user-management.md.

- [ ] A5. Danh mục: `GET /departments|subjects|topics` + CRUD `/admin/*`.

**Chặng 2 (25–29/11) — Quy trình**
- [ ] A6. State machine **câu hỏi** (mục 4.2) + endpoint `submit/withdraw/approve/reject/archive/mark-reviewed`, quy tắc "không tự duyệt", duyệt thay (3.1), xóa có điều kiện, `revision`; `GET /review/questions`.
- [ ] A7. `Exam`, `ExamVersion`, **snapshot khi Submit** + `content_hash`, kiểm tra mọi câu `Approved`, tổng điểm; state machine phiên bản (4.3): `submit/withdraw/approve/reject/archive/publish/unpublish`, partial unique 1 bản `Published`; `POST /exams/{id}/versions`; `GET /review/exam-versions`.
- [ ] A8. Audit log mọi chuyển trạng thái + `GET /audit-logs` (Head: trong tổ).
- [ ] A9. **Test**: unit cho mọi chuyển trạng thái hợp lệ/không hợp lệ và ma trận quyền (mục 3.2); integration với PostgreSQL thật.
- [ ] A10. Frontend: màn admin (user, tổ, môn, chương), ngân hàng câu hỏi (danh sách/lọc/sửa), màn **duyệt** câu và đề (checklist, cờ `ai_verification`, nhập lý do từ chối), hàng đợi duyệt.

## DEV-B — AI + soạn đề + xuất đề (@flwndyy)

**Chặng 1 (20–24/11) — Làm song song, không phụ thuộc DEV-A**
- [x] B1. Next.js static export: `trailingSlash`, Tailwind, TanStack Query, React Hook Form + Zod, KaTeX; client gọi API sinh từ OpenAPI; layout + chuỗi tiếng Việt tách file; route động bằng query string (mục 9.2).
- [ ] B2. Luồng đăng nhập Cognito Authorization Code + PKCE: callback, refresh token, hết hạn, đăng xuất, auth guard (UX); CSP chặt. *Cần Cognito + domain từ OPS.*
- [ ] B3. **Lõi AI (thuần logic, test được ngay)**: prompt template có `prompt_version`; JSON schema + validator theo từng dạng (mục 8.2); phân loại lỗi retryable/non-retryable; interface Bedrock + **fake** cho test.
- [ ] B4. Kiểm chứng đáp án (tùy chọn, mục 4.1.2) và loại trùng bằng so khớp chuỗi.

**Chặng 2 (25–29/11) — Pipeline và tính năng** · B11 thuộc **Chặng 3 (30/11–01/12)**
- [ ] B5. `POST /ai/generate`, `GET /ai/jobs`, `GET /ai/jobs/{id}`, `GET /ai/quota`: kiểm quota (Parameter Store) trước khi enqueue, tạo job + gửi SQS, rate limit, giới hạn input. *Cần entity/DB của DEV-A (A2).*
- [ ] B6. **Worker** (mục 4.1.1): claim idempotent bằng `UPDATE` có điều kiện, lease + heartbeat, lưu câu + đổi trạng thái job trong **một transaction**, chỉ xóa message sau commit, `ChangeMessageVisibility` backoff, DLQ, tác vụ phục hồi job treo và gửi lại job kẹt `Queued`, ghi token theo từng lần gọi, `ai_usage_daily`.
- [ ] B7. Test worker: giao trùng message, crash giữa chừng, retry/DLQ, vượt quota (fake Bedrock + PostgreSQL thật).
- [ ] B8. Frontend AI: form sinh câu, polling job (backoff, Queued/Running/Failed), rời trang quay lại vẫn xem được, rà soát từng câu + nút **"Đã rà soát"**, sanitize Markdown/KaTeX, `/admin/ai-usage`.
- [ ] B9. **Soạn đề**: `GET/POST /exams`, `GET/PUT /exams/{id}`, `PUT /exam-versions/{vid}` (chọn/sắp xếp câu, điểm; chỉ khi `Draft`/`Rejected`), `GET /exams/{id}/versions`; frontend ghép đề, cảnh báo câu nguồn đổi `revision`. *Phối hợp A7 về entity và snapshot.*
- [ ] B10. **Xuất đề**: `student-sheet` và `answer-key` đọc từ snapshot, kiểm quyền riêng, trang xem trước có CSS in + KaTeX, PDF qua trình duyệt, **đảm bảo đáp án không lọt vào đề học sinh** (có test).
- [ ] B11. E2E luồng demo chính (mục 2.1).

## OPS-1 — Mạng, edge, compute (@HuuPhuoc-NH)

- [ ] O1. Chọn và dựng khung IaC theo quyết định mục 14 (hoặc quy trình dựng tay có ghi lại trong `docs/`); đặt trong `/infra`, **không chứa ID/ARN thật** (B8).
- [ ] O2. VPC: public subnet cho EC2, private subnet cho RDS, **không NAT Gateway**; Security Group: EC2 443 chỉ từ prefix list CloudFront, 80 chỉ cho Let's Encrypt, **không mở SSH**; RDS chỉ nhận SG của EC2 (E3, mục 5.3). Kiểm tra quota rule của SG.
- [ ] O3. EC2 + EBS + Elastic IP + SSM Session Manager + instance profile least-privilege (SQS, Bedrock, SSM, Cognito Admin*) (mục 5.2).
- [ ] O4. Domain + DNS (`app.`, `origin.`), ACM (us-east-1), Nginx + Let's Encrypt + timer gia hạn, kiểm tra header `X-Origin-Verify`, `ForwardedHeaders` (C2, mục 5.3).
- [ ] O5. S3 (OAC) + CloudFront: behavior `/api/*` (CachingDisabled, AllViewerExceptHostHeader, đủ HTTP methods, custom header) và `/*` (S3 + **CloudFront Function** thêm `index.html`, **không** dùng custom error 403/404) (E2).
- [ ] O6. GitHub OIDC provider + role deploy, trust policy khóa theo repo + nhánh `main` (E1).
- [ ] O7. CI/CD **frontend**: build → `s3 sync` → invalidation, giữ artifact bản trước để rollback (E8). *Cần B1.*
- [ ] O8. Dựng/thử luồng đăng nhập từ đầu đến cuối với DEV-B (callback URL đúng domain).

## OPS-2 — Dữ liệu, danh tính, giám sát, chi phí (@TranGiaBao2005)

- [x] O9. **Cognito** (E4): tắt self sign-up, 3 group `teacher/head/admin`, app client PKCE, mật khẩu tạm + đổi lần đầu, quên mật khẩu. Giao `UserPoolId/ClientId` cho dev qua Parameter Store, không qua chat công khai. **Chặn A4 và B2 → ưu tiên sớm.**
- [ ] O10. **RDS PostgreSQL** Single-AZ, private subnet, mã hóa, backup tự động ngắn, thử khôi phục một lần (E5, mục 12).
- [ ] O11. **SQS + DLQ** (`maxReceiveCount=3`, visibility timeout > thời gian xử lý tối đa) (C4, mục 4.1.1).
- [ ] O12. **Parameter Store**: quy ước đặt tên, connection string, ModelId, quota, ID Cognito, origin secret; dùng key AWS-managed.
- [ ] O13. **CloudWatch** (E6): log retention 7 ngày; alarm DLQ depth, tuổi message cũ nhất, 5xx, CPU/EBS, RDS storage/connections, **heartbeat worker**, số job `Running` quá lease; giới hạn số alarm/metric.
- [ ] O14. **AWS Budgets** 50/80/100% của $200, gửi email cả nhóm (E7).
- [ ] O15. CI/CD **backend** (E8): build/publish theo kiến trúc đã chọn, giải nén vào `/opt/exammaker/releases/<id>`, đổi symlink, restart systemd (API + Worker), kiểm tra health, giữ N bản để rollback; **EF migration bundle** chạy trước, tương thích ngược, snapshot RDS thủ công trước migration lớn. *Cần A1.*
- [ ] O16. Dọn tài nguyên sau demo: snapshot, EBS rời, Elastic IP không gắn; nhắc RDS tự khởi động lại sau 7 ngày dừng.

---

## Thứ tự và phụ thuộc

```
C0 (17–19/11): 0.1 ─ 0.2 ─ 0.3 ─ 0.4 ─ 0.5 ─ 0.6        (cả nhóm)
        │     │
C1 (20–24/11): DEV-A: A1→A2→A3→A5, A4     DEV-B: B1, B3, B2
               OPS-2: O9 (sớm) ,O10,O11,O12   OPS-1: O1→O2→O3→O4→O5
        │
C2 (25–29/11): DEV-A: A6→A7→A8→A9→A10    DEV-B: B5→B6→B7→B8, B9→B10
               OPS-1: O6,O7,O8              OPS-2: O13,O14,O15
        │
C3 (30/11–01/12): B11 E2E, B4 (nếu kịp), thử khôi phục, O16, kiểm tra ngân sách, demo
```

Các điểm chặn chính:
- **0.2 + 0.3 → A2, B1, B5, B9** (cả hai dev bám cùng contract).
- **A2 (DB) → B5, B6, B9**: DEV-B làm B1–B4 trước để không phải chờ.
- **O9 (Cognito) → A4, B2**; **O10, O11, O12 → A1, B5, B6** ở môi trường thật (local dùng PostgreSQL trong Docker + fake SQS/Bedrock).
- **A7 ↔ B9**: A7 sở hữu entity/state machine/snapshot, B9 sở hữu endpoint soạn đề. Thống nhất DTO trong OpenAPI trước khi code.

## Ranh giới để tránh xung đột merge

- `backend/src/ExamMaker.Domain|Application|Infrastructure` (entity, state machine, migration): **DEV-A** sở hữu. DEV-B cần đổi thì mở PR nhỏ riêng, DEV-A review. **Mỗi migration chỉ một người tạo tại một thời điểm**, báo trước trong nhóm.
- `backend/src/ExamMaker.Worker` và thư mục AI trong `Application`/`Infrastructure`: **DEV-B**.
- `frontend/`: **DEV-B** sở hữu khung (B1, B2); DEV-A thêm màn hình trong thư mục tính năng riêng (`admin`, `questions`, `review`), không sửa khung nếu chưa báo.
- `infra/`, `.github/`: **OPS** (đúng CODEOWNERS); dev chỉ nhờ thêm/sửa workflow qua PR.
- Mỗi PR nhỏ, một việc (một ID trong bảng trên), tên nhánh `feat/…`, `fix/…`, `infra/…` tách từ `dev`; PR vào `dev` merge bằng Squash, PR `dev` → `main` chỉ dùng Merge commit (xem `CONTRIBUTING.md`).

## Định nghĩa "xong"

- CI xanh, có test cho phần quy tắc nghiệp vụ liên quan (mục 12), không secret/ID thật trong diff.
- Đổi hành vi/quy tắc → cập nhật `docs/context.md` hoặc OpenAPI **trong cùng PR**.
- Với việc hạ tầng: ghi lại cách dựng/kiểm tra trong `docs/` hoặc IaC, không chỉ làm tay trên Console.
