using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Ai;

// Tổng hợp quota theo người dùng và ngày UTC, không chứa thông tin đăng nhập.
public class AiUsageDaily
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public DateOnly Date { get; set; }
    public int Jobs { get; set; }
    public long Tokens { get; set; }
}
