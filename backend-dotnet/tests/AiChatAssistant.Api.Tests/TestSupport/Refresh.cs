using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Tests.TestSupport;

/// <summary>A real <see cref="RefreshTokenService"/> over the test's own (in-memory) context, so controller tests exercise the actual rotation rules.</summary>
public static class Refresh
{
    public static RefreshTokenService Service(AppDbContext db, TimeProvider? time = null, RefreshTokenOptions? options = null) =>
        new(db, Options.Create(options ?? new RefreshTokenOptions()), time ?? TimeProvider.System, NullLogger<RefreshTokenService>.Instance);
}
