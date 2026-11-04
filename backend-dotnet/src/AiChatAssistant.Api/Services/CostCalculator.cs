using AiChatAssistant.Api.Models;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Services;

/// <summary>Turns the token counts the AI service reports into a dollar estimate, using the configured prices, and builds the usage row.</summary>
public sealed class CostCalculator
{
    private const int Decimals = 8;
    private readonly PricingOptions _pricing;

    public CostCalculator(IOptions<PricingOptions> pricing)
    {
        _pricing = pricing.Value;
    }

    /// <summary>A calculator with no prices configured: every cost is zero. Used when nothing was registered (and by tests that do not care).</summary>
    public static CostCalculator Unpriced { get; } = new(Options.Create(new PricingOptions()));

    /// <summary>
    /// <c>prompt x input rate + completion x output rate</c>, per million. If the service reported only a total (no split), the whole total is
    /// billed at the higher of the two rates, so the estimate errs on the side of too much rather than too little.
    /// </summary>
    public decimal Estimate(string model, int promptTokens, int completionTokens, int totalTokens)
    {
        var (input, output) = RatesFor(model);
        promptTokens = Math.Max(promptTokens, 0);
        completionTokens = Math.Max(completionTokens, 0);

        decimal cost;
        if (promptTokens + completionTokens == 0)
        {
            cost = Math.Max(totalTokens, 0) * Math.Max(input, output) / 1_000_000m;
        }
        else
        {
            cost = (promptTokens * input + completionTokens * output) / 1_000_000m;
        }
        return Math.Round(cost, Decimals);
    }

    public UsageLog CreateLog(int userId, Guid sessionId, int totalTokens, int promptTokens, int completionTokens, string? model) => new()
    {
        UserId = userId,
        SessionId = sessionId,
        TokensUsed = totalTokens,
        PromptTokens = Math.Max(promptTokens, 0),
        CompletionTokens = Math.Max(completionTokens, 0),
        Model = CleanModel(model),
        CostEstimate = Estimate(model ?? string.Empty, promptTokens, completionTokens, totalTokens),
    };

    private static string CleanModel(string? model)
    {
        var name = (model ?? string.Empty).Trim();
        return name.Length > 100 ? name[..100] : name; // the column holds 100 characters
    }

    private (decimal Input, decimal Output) RatesFor(string model)
    {
        var match = _pricing.Models.FirstOrDefault(kv => string.Equals(kv.Key, model, StringComparison.OrdinalIgnoreCase));
        return match.Value is { } price
            ? (price.InputPerMillion, price.OutputPerMillion)
            : (_pricing.InputPerMillion, _pricing.OutputPerMillion);
    }
}
