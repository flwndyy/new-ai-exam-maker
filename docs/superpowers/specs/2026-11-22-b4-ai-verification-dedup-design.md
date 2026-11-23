# B4 — Kiểm chứng đáp án AI & loại câu trùng (thiết kế)

Ngày: 22/11/2026 · Task: B4 (`docs/tasks.md`) · Ngữ cảnh: `docs/context.md` mục 4.1.2

## Mục đích

AI có thể sai đáp án và có thể sinh lại câu đã có. B4 thêm hai lớp kiểm tra **trước khi** câu AI được lưu thành `Draft`:

1. **Kiểm chứng đáp án** (tùy chọn, bật/tắt bằng cấu hình — tắt khi dev, bật khi demo): model tự giải câu hỏi (không xem đáp án), so với đáp án gốc → `ai_verification` = `Agree` / `Disagree` / `NotChecked`. `Disagree` là cờ "cần xem kỹ" cho người duyệt.
2. **Loại trùng** bằng so khớp chuỗi đã chuẩn hóa: bỏ câu trùng trong cùng lô và câu trùng với ngân hàng (cùng môn + chương).

Lý do làm bây giờ: B4 từng bị cắt khỏi 2 tuần, nhưng nhóm xác định còn thời gian.

## Mục tiêu / Không làm / Thất bại nếu

- **Mục tiêu**: logic thuần ở Application (giống B3), test bằng fake model; worker B6 chỉ cần gọi theo thứ tự. Thêm đúng **1** lần gọi model cho kiểm chứng mỗi job.
- **Không làm**: so trùng bằng embedding/độ tương tự; kiểm chứng `Essay`; sinh câu thay thế cho câu bị loại; gắn B4 vào worker (thuộc B6); giao diện hiển thị (thuộc frontend).
- **Thất bại dù chạy được**: kiểm chứng lỗi làm job `Failed` hoặc kích hoạt retry sinh lại cả job (tốn token gấp đôi); loại nhầm câu đúng/sai chỉ vì đề bài chung chung; đáp án/đầu ra model lọt vào `error_message`.

## Các hướng đã cân nhắc

| Hướng | Đánh đổi | Kết quả |
|---|---|---|
| **1. Ba khối riêng** (`AiQuestionGenerator` giữ nguyên + `AiDuplicateFilter` + `AiAnswerVerifier`), worker ghép: sinh → lọc trùng → kiểm chứng | Mỗi khối test riêng, không đụng B3; worker phải gọi đúng thứ tự | **Đã chọn** |
| 2. Gộp vào `AiQuestionGenerator` | Một lời gọi cho worker, nhưng generator phải biết DB + cờ cấu hình, phải sửa B3 | Bỏ |
| 3. Kiểm chứng mỗi câu một lần gọi | Lỗi cô lập theo câu, nhưng N lần gọi: tốn token, dễ chạm visibility timeout 300s | Bỏ |

Xử lý câu trùng với ngân hàng — đã chọn **A. bỏ luôn** (không gắn cờ, không sinh bù). So khớp chính xác nên câu bị bỏ không mang nội dung mới.

## Thiết kế

### Thứ tự trong worker (B6 gắn vào)

```
AiQuestionGenerator.GenerateAsync  →  AiDuplicateFilter.Filter(questions, existingKeys)
  →  (nếu VerifyAnswers) AiAnswerVerifier.VerifyAsync(kept)  →  lưu Draft + job trong 1 transaction
```

Lọc trùng trước để không tốn token kiểm chứng câu sẽ bỏ.

### Loại trùng — `Application/Ai/Generation/AiDuplicateFilter` (thuần)

- **Khóa trùng** = chuẩn hóa(stem) + nội dung phần đề trong payload:
  - `MultipleChoice`: text các phương án theo thứ tự nhãn A–D.
  - `TrueFalse`: text các ý theo thứ tự a–d (đề bài TF thường chung chung như "Cho các phát biểu sau").
  - `ShortAnswer`, `Essay`: chỉ stem.
  - Không đưa đáp án (`correctLabel`, `isTrue`, `acceptedAnswers`…) vào khóa.
- **Chuẩn hóa**: NFC → lowercase invariant → gộp mọi chuỗi khoảng trắng thành một dấu cách → trim. Giữ nguyên dấu câu và LaTeX.
- Đầu vào: danh sách câu từ generator + tập khóa đã có. Đầu ra: câu giữ lại (**giữ nguyên `ordinal` gốc**, chấp nhận khoảng trống) + số câu bị bỏ. Trong lô có trùng thì giữ câu có ordinal nhỏ nhất.
- **Tập khóa ngân hàng**: Infrastructure thêm truy vấn đọc stem/type/payload của **mọi câu cùng `subject_id` + `topic_id`** (mọi trạng thái, mọi tác giả), tính khóa bằng cùng hàm chuẩn hóa ở C# (không chuẩn hóa trong SQL để tránh lệch). Quy mô vài nghìn câu nên đọc trong bộ nhớ là đủ; không thêm cột hash/index.
- Hai job song song cùng chương có thể cùng chèn một câu trùng: chấp nhận (MVP, không unique index).
- Tất cả câu bị bỏ → job vẫn `Succeeded` với 0 câu.

### Số câu bị bỏ — migration nhỏ

- Thêm cột `ai_generation_jobs.duplicates_dropped integer NOT NULL DEFAULT 0`, CHECK `>= 0` (chỉ thêm cột, tương thích ngược).
- OpenAPI `AiJob` thêm `duplicatesDropped` (integer, min 0, required) để giao diện báo "đã bỏ N câu trùng". Endpoint đọc job thuộc B5.

### Kiểm chứng — `Application/Ai/Verification/AiAnswerVerifier`

- Cấu hình: `AiGeneration:VerifyAnswers` (bool, mặc định `false`; Parameter Store bật khi demo). Tắt → mọi câu `NotChecked`, note null, không gọi model.
- **Một lần gọi cho cả lô**, chỉ gồm câu `MultipleChoice`/`TrueFalse`/`ShortAnswer`; không có câu nào kiểm được → không gọi. Dùng chung `IAiModelClient`, `Temperature` 0, `MaxOutputTokens` riêng (mặc định 2048, cấu hình được).
- **Prompt** có phiên bản riêng `answer-verify-v1`: gửi `ordinal`, dạng, stem, và phần đề trong payload (phương án / các ý / `answerType`+`unit`) — **không** gửi đáp án, lời giải. Nội dung câu là đầu ra AI nên mã hóa chuỗi JSON và đặt trong khối dữ liệu như ghi chú giáo viên ở B3.
- **Đầu ra mong đợi**: `{"answers":[{"ordinal":1,"answer":"B"}, {"ordinal":2,"answer":{"a":true,"b":false,"c":true,"d":false}}, {"ordinal":3,"answer":"12,5"}]}`. Parser chặt như B3 (khóa thừa/thiếu/trùng, đúng kiểu theo dạng câu).
- **So sánh theo dạng**:
  - MC: nhãn model == `correctLabel` → `Agree`, khác → `Disagree`.
  - TF: cả 4 ý khớp → `Agree`; lệch ý nào → `Disagree`, note liệt kê nhãn lệch.
  - ShortAnswer `number`: đổi `,`→`.`, parse decimal; khớp nếu `|model − accepted| ≤ tolerance` (không có tolerance → bằng tuyệt đối) với **bất kỳ** đáp án chấp nhận. Không parse được → `Disagree`.
  - ShortAnswer `text`: chuẩn hóa như loại trùng rồi so bằng với bất kỳ đáp án chấp nhận.
  - `Essay`: luôn `NotChecked`, note null.
- **Note** (`ai_verification_note`): câu tiếng Việt cố định theo mẫu, vd "Mô hình tự giải chọn C, đáp án gốc là B.", "Khác đáp án gốc ở ý: b, d.", "Mô hình tự giải ra \"…\", không khớp đáp án chấp nhận." (giá trị của model cắt tối đa 100 ký tự; là dữ liệu không tin cậy, frontend sanitize như nội dung AI khác). `Agree` → note null.
- **Lỗi không làm hỏng job**: `AiModelException` (mọi loại), hết thời gian, JSON/schema sai, model dừng do token/lọc nội dung → mọi câu trong lô `NotChecked`, note "Không kiểm chứng được tự động." Không có vòng sửa lại. Câu bị model bỏ sót → riêng câu đó `NotChecked`, note "Mô hình không trả lời câu này."
- **Không nuốt**: `OperationCanceledException` từ token hủy của worker (tắt máy) và exception từ callback token (vd vượt trần token ngày) — đi lên worker như B3.
- **Token**: gọi cùng callback `onModelCall` sau lần gọi kiểm chứng để worker cộng vào job/`ai_usage_daily`. Job giữ `prompt_version` của prompt sinh câu (không thêm cột cho prompt kiểm chứng; phiên bản kiểm chứng ghi trong code).
- Kết quả: `IReadOnlyDictionary<int ordinal, (string Verification, string? Note)>` để worker gán vào `Question` khi lưu.

### Kiểm thử

- Unit (Application.Tests, fake model): khóa trùng từng dạng, TF đề chung chung nhưng ý khác không bị loại, trùng trong lô/với ngân hàng, giữ ordinal; so sánh từng dạng (tolerance, dấu phẩy, text chuẩn hóa); prompt không chứa đáp án/lời giải; parser từ chối schema sai; mọi nhánh lỗi → `NotChecked`; cờ tắt → không gọi model; callback được gọi đúng 1 lần; cancel/callback exception được ném lại.
- Integration (PostgreSQL thật): truy vấn tập khóa theo môn/chương; migration + CHECK `duplicates_dropped >= 0`.

### Tài liệu

- Sửa `docs/context.md` **trước khi code**: mục 4.1.2 thêm "Chi tiết triển khai B4"; mục 8.1 thêm `duplicates_dropped`; tăng phiên bản 0.18 + lịch sử thay đổi.
- `docs/api/openapi.yaml`: `AiJob.duplicatesDropped`. `docs/tasks.md`: tick B4 khi xong.

## Quyết định do Claude tự chọn (người dùng có thể sửa)

- Khóa trùng gồm stem + text phương án/ý; so với mọi câu cùng môn + chương, mọi trạng thái/tác giả.
- Cột `duplicates_dropped`; tất cả trùng → `Succeeded` với 0 câu; giữ ordinal gốc.
- Kiểm chứng không sửa lại, lỗi → `NotChecked`; temperature 0; note tiếng Việt cố định kèm giá trị model ≤ 100 ký tự.

## Để lại cho người làm

Tên lớp/namespace chính xác, nội dung câu chữ prompt kiểm chứng, cách tách hàm chuẩn hóa dùng chung, tên migration.
