using ExamMaker.Application.Ai.Worker;
using ExamMaker.Infrastructure.Ai;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class SqsAiJobQueueTests
{
    [Fact]
    public void Worker_parses_the_body_the_api_sends()
    {
        var jobId = Guid.NewGuid();

        Assert.True(AiJobMessageHandler.TryParseJobId(SqsAiJobQueue.CreateMessageBody(jobId), out var parsed));
        Assert.Equal(jobId, parsed);
    }
}
