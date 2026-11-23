using System;
using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Ai
{
    // Tracks asynchronous generation, retry leases and cumulative token usage.
    public class AiGenerationJob
    {
        public Guid Id { get; set; }

        public Guid RequestedBy { get; set; }

        public User? Requester { get; set; }

        // Serialized generation parameters, persisted as PostgreSQL jsonb.
        public string Params { get; set; } = "{}";

        public string Status { get; set; } = "Queued";

        public int Attempt { get; set; }

        public int MaxAttempts { get; set; } = 3;

        // Running jobs require a lease so the worker can recover abandoned work.
        public DateTime? LeaseExpiresAt { get; set; }

        public string? ErrorCode { get; set; }

        public string? ErrorMessage { get; set; }

        public string? ModelId { get; set; }

        public string? PromptVersion { get; set; }

        // Cumulative usage includes all attempts, including retried model calls.
        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        // Generated questions discarded as duplicates (within the batch or against the bank).
        public int DuplicatesDropped { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Last time a message for this job was sent to SQS; the recovery task re-sends stale Queued jobs.
        public DateTime LastEnqueuedAt { get; set; } = DateTime.UtcNow;

        public DateTime? StartedAt { get; set; }

        public DateTime? FinishedAt { get; set; }
    }
}