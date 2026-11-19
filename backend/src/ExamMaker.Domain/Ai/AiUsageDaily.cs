using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Ai;

// One quota counter per user and UTC date.
public class AiUsageDaily
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public DateOnly Date { get; set; }
    public int Jobs { get; set; }
    public long Tokens { get; set; }
}
