namespace ExamMaker.Application.Ai;

/// <summary>User-facing validation messages (Vietnamese), kept apart from the validation logic.</summary>
internal static class AiMessages
{
    public const string SubjectRequired = "Vui lòng chọn môn học.";

    public const string TopicRequired = "Vui lòng chọn chương.";

    public const string GradeInvalid = "Khối phải là 10, 11 hoặc 12.";

    public const string TypeInvalid = "Dạng câu hỏi không hợp lệ.";

    public const string DifficultyInvalid = "Mức độ nhận thức không hợp lệ.";

    public static string CountOutOfRange(int max) => $"Số câu phải từ 1 đến {max}.";

    public static string NoteTooLong(int max) => $"Ghi chú tối đa {max} ký tự.";

    public const string SubjectNotFound = "Môn học không tồn tại.";

    public const string TopicNotFound = "Chương không tồn tại.";

    public const string TopicNotInSubject = "Chương không thuộc môn học đã chọn.";

    public const string GradeNotMatchingTopic = "Khối không khớp với khối của chương.";

    public const string PageInvalid = "Trang phải lớn hơn hoặc bằng 1.";

    public static string PageSizeInvalid(int max) => $"Số phần tử mỗi trang phải từ 1 đến {max}.";

    public const string JobStatusInvalid = "Trạng thái job không hợp lệ.";

    /// <summary>Job failure text stored in ai_generation_jobs.error_message; never contains model or provider output.</summary>
    public static string GenerationFailed(string code) => code switch
    {
        "Throttled" => "Dịch vụ AI đang quá tải, hệ thống sẽ thử lại.",
        "Timeout" => "Dịch vụ AI phản hồi quá lâu, hệ thống sẽ thử lại.",
        "ServiceUnavailable" => "Dịch vụ AI tạm thời không khả dụng, hệ thống sẽ thử lại.",
        "NetworkError" => "Lỗi kết nối tới dịch vụ AI, hệ thống sẽ thử lại.",
        "InvalidRequest" => "Yêu cầu gửi tới dịch vụ AI không hợp lệ.",
        "AccessDenied" => "Hệ thống chưa được cấp quyền dùng mô hình AI.",
        "ContentRefused" => "Mô hình AI từ chối tạo nội dung cho yêu cầu này.",
        "InvalidJson" => "Kết quả AI không đúng định dạng JSON.",
        "SchemaViolation" => "Kết quả AI không đúng cấu trúc câu hỏi.",
        "OutputTruncated" => "Kết quả AI bị cắt ngắn, hãy giảm số câu rồi thử lại.",
        _ => "Lỗi không xác định khi sinh câu hỏi.",
    };
}
