namespace AiChatAssistant.Api.Services;

/// <summary>
/// What the AI provider charges, per million tokens, in US dollars (config section "Pricing"). The numbers are an estimate kept in
/// configuration so a price change never needs a deployment: check them against the provider's price list for the model in use.
/// </summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>The rate for any model not listed in <see cref="Models"/>: dollars per million tokens sent (question and history).</summary>
    public decimal InputPerMillion { get; set; }

    /// <summary>The rate for any model not listed in <see cref="Models"/>: dollars per million tokens received (the answer).</summary>
    public decimal OutputPerMillion { get; set; }

    /// <summary>Rates for particular models, by the model name the AI service reports (case-insensitive).</summary>
    public Dictionary<string, ModelPrice> Models { get; set; } = new();
}

public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}
