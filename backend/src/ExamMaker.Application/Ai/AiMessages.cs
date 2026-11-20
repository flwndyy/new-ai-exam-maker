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

    public const string PageInvalid = "Trang phải lớn hơn hoặc bằng 1.";

    public static string PageSizeInvalid(int max) => $"Số phần tử mỗi trang phải từ 1 đến {max}.";

    public const string JobStatusInvalid = "Trạng thái job không hợp lệ.";
}
