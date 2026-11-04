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

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    /// <summary>Empty for rows written before cost tracking existed: the page shows "Not tracked" for those.</summary>
    public string Model { get; set; } = string.Empty;

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

public class UsageTotals
{
    public int Requests { get; set; }

    public long Tokens { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    public decimal Cost { get; set; }
}

public class UsageByUser
{
    public int UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string UserEmail { get; set; } = string.Empty;

    public int Requests { get; set; }

    public long Tokens { get; set; }

    public decimal Cost { get; set; }
}

public class UsageByDay
{
    /// <summary>The UTC day, <c>yyyy-MM-dd</c>.</summary>
    public string Date { get; set; } = string.Empty;

    public int Requests { get; set; }

    public long Tokens { get; set; }

    public decimal Cost { get; set; }
}

/// <summary>Totals computed by the database over the whole filtered range, not over one page of rows.</summary>
public class UsageSummaryResponse
{
    public UsageTotals Totals { get; set; } = new();

    /// <summary>The biggest spenders, at most 50, most expensive first.</summary>
    public IReadOnlyList<UsageByUser> ByUser { get; set; } = Array.Empty<UsageByUser>();

    /// <summary>One entry per UTC day that had activity, oldest first; at most the newest 366 days.</summary>
    public IReadOnlyList<UsageByDay> ByDay { get; set; } = Array.Empty<UsageByDay>();
}

public class AdminUserResponse
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    public DateTime CreatedAt { get; set; }

    public DateTime? LastSeenAt { get; set; }

    /// <summary>When the account was disabled; null for an active account.</summary>
    public DateTime? DisabledAt { get; set; }
}

public class PagedAdminUsersResponse
{
    public IReadOnlyList<AdminUserResponse> Items { get; set; } = Array.Empty<AdminUserResponse>();

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }

    public int TotalPages { get; set; }
}

public class ChangeRoleRequest
{
    /// <summary>"Admin" or "User".</summary>
    public string Role { get; set; } = string.Empty;
}
