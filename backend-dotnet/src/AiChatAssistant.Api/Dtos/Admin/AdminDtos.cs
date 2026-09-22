namespace AiChatAssistant.Api.Dtos.Admin;

public class UsageLogResponse
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string UserEmail { get; set; } = string.Empty;

    public Guid SessionId { get; set; }

    public string SessionTitle { get; set; } = string.Empty;

    public int TokensUsed { get; set; }

    public decimal CostEstimate { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class PagedUsageLogResponse
{
    public IReadOnlyList<UsageLogResponse> Items { get; set; } = Array.Empty<UsageLogResponse>();

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }

    public int TotalPages { get; set; }
}
