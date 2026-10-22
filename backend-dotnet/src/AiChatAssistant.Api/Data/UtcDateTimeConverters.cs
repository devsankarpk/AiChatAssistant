using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AiChatAssistant.Api.Data;

/// <summary>
/// Every date in this app is stored as UTC (the code writes <c>DateTime.UtcNow</c>), but MySQL's
/// <c>datetime(6)</c> has no time zone, so EF reads values back with <c>Kind = Unspecified</c>. The JSON
/// serialiser then writes them WITHOUT a trailing "Z" ("2026-10-21T15:13:56"), and browsers read a bare
/// timestamp as LOCAL time, so everyone outside UTC saw every time off by their UTC offset. Marking
/// values as UTC on the way out makes the API send "...Z", which browsers convert correctly.
/// </summary>
public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            write => write.Kind == DateTimeKind.Local ? write.ToUniversalTime() : write,
            read => DateTime.SpecifyKind(read, DateTimeKind.Utc))
    {
    }
}

public class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeConverter()
        : base(
            write => write.HasValue && write.Value.Kind == DateTimeKind.Local ? write.Value.ToUniversalTime() : write,
            read => read.HasValue ? DateTime.SpecifyKind(read.Value, DateTimeKind.Utc) : read)
    {
    }
}
