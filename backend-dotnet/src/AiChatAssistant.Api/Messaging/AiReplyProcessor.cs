using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Chat;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Messaging;

public enum AiReplyOutcome
{
    /// <summary>The reply was produced, saved and pushed.</summary>
    Done,

    /// <summary>The job was already done or failed, or another worker took it. Nothing to do, acknowledge.</summary>
    AlreadyHandled,

    /// <summary>No such job (deleted with its chat). Acknowledge and forget.</summary>
    Missing,

    /// <summary>The AI call failed and retries remain. Park the job for <see cref="AiReplyResult.Delay"/>.</summary>
    Retry,

    /// <summary>The AI call failed and no retries remain. The job is marked failed; move it to the dead-letter queue.</summary>
    Failed,

    /// <summary>Another worker holds a fresh claim. Look again after <see cref="AiReplyResult.Delay"/>.</summary>
    Deferred,
}

public sealed record AiReplyResult(AiReplyOutcome Outcome, TimeSpan Delay = default, string? Error = null);

/// <summary>
/// The worker's decisions for one job, free of RabbitMQ so it can be tested without a broker. It is what makes
/// redelivery harmless: a done job is never answered twice, a job claimed by a live worker is left alone, a
/// claim abandoned by a dead worker is taken over after the lease, and two workers racing for a job cannot both
/// win (the claim token is a concurrency token). The question is read from the database, never from the queue.
/// </summary>
public sealed class AiReplyProcessor
{
    public const string FailureCode = "ai_service_unavailable";
    public const string FailureMessage = "The assistant couldn't reply this time.";

    private readonly AppDbContext _db;
    private readonly IPythonAiClient _python;
    private readonly IChatNotifier _notifier;
    private readonly ChatOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AiReplyProcessor> _logger;

    public AiReplyProcessor(
        AppDbContext db,
        IPythonAiClient python,
        IChatNotifier notifier,
        IOptions<ChatOptions> options,
        TimeProvider time,
        ILogger<AiReplyProcessor> logger)
    {
        _db = db;
        _python = python;
        _notifier = notifier;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<AiReplyResult> ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.AiReplyJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            _logger.LogInformation("AI reply job {JobId} no longer exists; dropping the message", jobId);
            return new AiReplyResult(AiReplyOutcome.Missing);
        }

        if (job.Status is AiReplyJobStatus.Done or AiReplyJobStatus.Failed)
        {
            _logger.LogInformation("AI reply job {JobId} is already {Status}; ignoring the duplicate delivery", jobId, job.Status);
            return new AiReplyResult(AiReplyOutcome.AlreadyHandled);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var lease = TimeSpan.FromSeconds(_options.ReplyLeaseSeconds);
        if (job.Status == AiReplyJobStatus.Processing && job.ClaimedAt is { } claimedAt && now - claimedAt < lease)
        {
            _logger.LogInformation("AI reply job {JobId} is being handled by another worker; looking again later", jobId);
            return new AiReplyResult(AiReplyOutcome.Deferred, lease);
        }

        // Claim it. The claim token is a concurrency token: if another worker claimed it between our read and
        // this save, the save fails and we leave the job to them.
        job.Status = AiReplyJobStatus.Processing;
        job.ClaimedAt = now;
        job.ClaimToken = Guid.NewGuid();
        job.Attempts++;
        job.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation("AI reply job {JobId} was claimed by another worker first", jobId);
            return new AiReplyResult(AiReplyOutcome.Deferred, lease);
        }

        var userMessage = await _db.Messages.FirstOrDefaultAsync(m => m.Id == job.UserMessageId, cancellationToken);
        if (userMessage is null)
        {
            return await MarkFailedAsync(job, "The question no longer exists.", cancellationToken);
        }

        var history = await ChatHistory.LoadAsync(_db, job.SessionId, job.UserMessageId, cancellationToken);

        PythonGenerateResult result;
        try
        {
            result = await _python.GenerateAsync(job.SessionId, history, userMessage.Content, cancellationToken);
        }
        catch (PythonServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "AI service unavailable for job {JobId} (attempt {Attempt})", jobId, job.Attempts);
            return await HandleFailureAsync(job, ex.Message, cancellationToken);
        }

        var assistantMessage = new Message
        {
            SessionId = job.SessionId,
            Role = MessageRole.Assistant,
            Content = result.Reply,
        };
        _db.Messages.Add(assistantMessage);
        _db.UsageLogs.Add(new UsageLog { UserId = job.UserId, SessionId = job.SessionId, TokensUsed = result.TokensUsed });
        job.AssistantMessage = assistantMessage;
        job.Status = AiReplyJobStatus.Done;
        job.Error = null;
        job.ClaimedAt = null;
        job.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        try
        {
            // Reply, usage log and job state commit together: there is no state in which the reply exists but the job is not done.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Our claim expired mid-call and another worker took the job over. Their answer stands; ours is discarded.
            _logger.LogWarning("AI reply job {JobId}: the claim was lost during the AI call; discarding this reply", jobId);
            return new AiReplyResult(AiReplyOutcome.AlreadyHandled);
        }

        await _notifier.AssistantRepliedAsync(job.UserId, job.Id, job.SessionId, job.UserMessageId, new MessageResponse
        {
            Id = assistantMessage.Id,
            Role = "assistant",
            Content = assistantMessage.Content,
            CreatedAt = assistantMessage.CreatedAt,
        }, cancellationToken);
        return new AiReplyResult(AiReplyOutcome.Done);
    }

    private async Task<AiReplyResult> HandleFailureAsync(AiReplyJob job, string reason, CancellationToken cancellationToken)
    {
        var delays = _options.ReplyRetryDelaysSeconds;
        if (job.Attempts <= delays.Length)
        {
            job.Status = AiReplyJobStatus.Pending;
            job.Error = Truncate(reason);
            job.ClaimedAt = null;
            job.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            if (!await TrySaveAsync(cancellationToken))
            {
                return new AiReplyResult(AiReplyOutcome.AlreadyHandled);
            }
            return new AiReplyResult(AiReplyOutcome.Retry, TimeSpan.FromSeconds(delays[job.Attempts - 1]), job.Error);
        }

        return await MarkFailedAsync(job, reason, cancellationToken);
    }

    private async Task<AiReplyResult> MarkFailedAsync(AiReplyJob job, string reason, CancellationToken cancellationToken)
    {
        job.Status = AiReplyJobStatus.Failed;
        job.Error = Truncate(reason);
        job.ClaimedAt = null;
        job.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        if (!await TrySaveAsync(cancellationToken))
        {
            return new AiReplyResult(AiReplyOutcome.AlreadyHandled);
        }

        _logger.LogError("AI reply job {JobId} failed after {Attempts} attempt(s): {Reason}", job.Id, job.Attempts, job.Error);
        await _notifier.AssistantFailedAsync(job.UserId, job.Id, job.SessionId, job.UserMessageId, FailureCode, FailureMessage, cancellationToken);
        return new AiReplyResult(AiReplyOutcome.Failed, Error: job.Error);
    }

    /// <summary>False when the claim was lost to another worker, in which case this one must stand down.</summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200];
}
