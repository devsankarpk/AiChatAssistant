using System.Text.Json;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Tests.TestSupport;

namespace AiChatAssistant.Api.Tests;

/// <summary>
/// Regression tests for a real bug found in a full test run: dates were stored in UTC but read back as
/// "Unspecified", so the API sent "2026-10-21T15:13:56" without a "Z" and every browser outside UTC showed
/// the time shifted by its UTC offset (and a value pushed live, which did carry a "Z", disagreed with the
/// same value after a reload).
/// </summary>
public class UtcDateTimeTests
{
    private static readonly DateTime Stored = new(2026, 10, 21, 15, 13, 56, 643, DateTimeKind.Unspecified);

    [Fact]
    public void ReadingADateFromTheDatabase_MarksItUtc_WithoutChangingTheValue()
    {
        var converter = new UtcDateTimeConverter();

        var read = (DateTime)converter.ConvertFromProvider(Stored)!;

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(Stored.Ticks, read.Ticks); // same moment: only the label changed
    }

    [Fact]
    public void ADateReadFromTheDatabase_IsSentToTheBrowserWithATrailingZ()
    {
        var read = (DateTime)new UtcDateTimeConverter().ConvertFromProvider(Stored)!;

        var json = JsonSerializer.Serialize(new { createdAt = read });

        Assert.Matches("\"createdAt\":\"2026-10-21T15:13:56\\.[0-9]+Z\"", json); // ends in Z, so a browser converts it correctly
    }

    [Fact]
    public void WritingALocalDate_ConvertsItToUtc_AndLeavesAUtcDateAlone()
    {
        var converter = new UtcDateTimeConverter();
        var local = new DateTime(2026, 10, 21, 20, 43, 0, DateTimeKind.Local);
        var utc = new DateTime(2026, 10, 21, 15, 13, 0, DateTimeKind.Utc);

        Assert.Equal(local.ToUniversalTime(), (DateTime)converter.ConvertToProvider(local)!);
        Assert.Equal(utc, (DateTime)converter.ConvertToProvider(utc)!);
    }

    [Fact]
    public void ANullableDate_KeepsNull_AndMarksAValueUtc()
    {
        var converter = new NullableUtcDateTimeConverter();

        Assert.Null(converter.ConvertFromProvider(null));
        var read = (DateTime?)converter.ConvertFromProvider((DateTime?)Stored);
        Assert.Equal(DateTimeKind.Utc, read!.Value.Kind);
        Assert.Null(converter.ConvertToProvider(null));
    }

    [Fact]
    public void EveryDateColumnInTheModel_UsesTheUtcConverter_SoNoneCanBeMissed()
    {
        using var db = TestDbContextFactory.Create();

        var dateProperties = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(dateProperties);
        Assert.All(dateProperties, p =>
        {
            var converter = p.GetValueConverter();
            Assert.True(
                converter is UtcDateTimeConverter || converter is NullableUtcDateTimeConverter,
                $"{p.DeclaringType.DisplayName()}.{p.Name} has no UTC converter");
        });
        // The ones this app actually has: guards against the test passing because the model is empty.
        Assert.Contains(dateProperties, p => p.Name == "LastSeenAt");
        Assert.Contains(dateProperties, p => p.Name == "DeliveredAt");
        Assert.Contains(dateProperties, p => p.Name == "CreatedAt");
    }

    [Fact]
    public void NewRows_GetTheirCreationTimeFromTheApp_InUtc_NotFromTheDatabaseClock()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var created = new DateTime[]
        {
            new User().CreatedAt, new ChatSession().CreatedAt, new Message().CreatedAt, new UsageLog().CreatedAt,
            new PasswordResetToken().CreatedAt, new DirectConversation().CreatedAt, new DirectMessage().CreatedAt,
        };

        Assert.All(created, c =>
        {
            Assert.Equal(DateTimeKind.Utc, c.Kind);
            Assert.InRange(c, before, DateTime.UtcNow.AddSeconds(1));
        });
    }
}
