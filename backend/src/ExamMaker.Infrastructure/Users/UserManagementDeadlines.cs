namespace ExamMaker.Infrastructure.Users;

public static class UserManagementDeadlines
{
    public static readonly TimeSpan Operation = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan AwsCall = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Compensation = TimeSpan.FromSeconds(5);

    /// <summary>Include lock waits and all normal remote calls in one cooperative operation budget.</summary>
    public static CancellationTokenSource Start(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(Operation);
        return source;
    }
}
