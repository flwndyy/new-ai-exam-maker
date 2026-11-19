using System;
using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Ai
{
    public class AiGenerationJob
    {
        // ID của yêu cầu sinh câu hỏi.
        public Guid Id { get; set; }

        // Người gửi yêu cầu.
        public Guid RequestedBy { get; set; }

        public User? Requester { get; set; }

        // Tham số đầu vào dạng JSON, sẽ ánh xạ sang cột jsonb.
        public string Params { get; set; } = "{}";

        // Trạng thái: Queued, Running, Succeeded hoặc Failed.
        public string Status { get; set; } = "Queued";

        // Số lượt xử lý đã bắt đầu.
        public int Attempt { get; set; }

        // Số lượt tối đa, khớp cấu hình retry trong tài liệu dự án.
        public int MaxAttempts { get; set; } = 3;

        // Thời hạn worker giữ quyền xử lý job.
        // Dùng để nhận biết và phục hồi job bị treo.
        public DateTime? LeaseExpiresAt { get; set; }

        // Thông tin lỗi khi xử lý thất bại.
        public string? ErrorCode { get; set; }

        public string? ErrorMessage { get; set; }

        // Model AI và phiên bản prompt đã sử dụng.
        public string? ModelId { get; set; }

        public string? PromptVersion { get; set; }

        // Tổng token cộng dồn qua các lần gọi AI.
        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        // Các thời điểm được lưu theo UTC.
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? StartedAt { get; set; }

        public DateTime? FinishedAt { get; set; }
    }
}