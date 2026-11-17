# Cài đặt Superpowers cho Claude Code, Codex và Antigravity

> Dành cho thành viên team (và AI coding agent). Superpowers là bộ skill quy trình làm việc cho coding agent (nguồn: [obra/superpowers](https://github.com/obra/superpowers)). **Mỗi công cụ cài riêng**, mỗi người cài trên máy của mình. Việc cài đặt này nằm ngoài repo, không commit gì liên quan.

> Các lệnh dưới đây lấy từ README chính thức tại thời điểm viết; công cụ thay đổi nhanh nên nếu lệnh lỗi, xem README mới nhất của repo.

---

## 1. Claude Code

Cài từ marketplace chính thức của Anthropic (tự cập nhật theo mặc định):

```
/plugin install superpowers@claude-plugins-official
```

Hoặc dùng marketplace của tác giả (mặc định **không** tự cập nhật):

```
/plugin marketplace add obra/superpowers-marketplace
/plugin install superpowers@superpowers-marketplace
```

## 2. Codex

- **Codex CLI**: chạy `/plugins`, tìm `superpowers`, chọn *Install Plugin*.
- **Codex App**: cài từ marketplace plugin chính thức của Codex.

## 3. Antigravity

```
agy plugin install https://github.com/obra/superpowers
```

- Cập nhật: chạy lại đúng lệnh trên.
- Plugin có hook chạy lúc bắt đầu phiên nên Superpowers hoạt động từ tin nhắn đầu tiên.
- Nếu lệnh `agy` không dùng được, có thể đặt thư mục skill thủ công vào `~/.gemini/antigravity/skills/` (mức user) hoặc `.agents/skills/` (mức workspace; bản cũ dùng `.agent/skills/`). Các đường dẫn này **chưa được xác nhận chắc chắn** — kiểm tra tài liệu mới nhất của Google trước khi dùng.

---

## Kiểm tra sau khi cài

Mở phiên mới ở từng công cụ và hỏi agent đang có những skill nào, hoặc giao một việc nhỏ (vd "lên kế hoạch cho một tính năng") và xem agent có dùng quy trình của Superpowers không.

## Lưu ý

- Superpowers chỉ là công cụ cho người phát triển, **không** phải phụ thuộc của dự án; không thêm vào `frontend/` hay `backend/`.
- Quy tắc của repo vẫn được ưu tiên: `CLAUDE.md` và `docs/context.md` (không tự commit/push, không commit secret, đổi nghiệp vụ thì sửa `docs/context.md` trước). Nếu skill gợi ý hành động trái các quy tắc này (vd tự commit) thì làm theo quy tắc của repo.
