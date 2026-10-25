namespace CheapHours.Persistence;

/// <summary>Converts between domain times (UTC <see cref="DateTimeOffset"/>) and BSON dates (UTC <see cref="DateTime"/>, millisecond precision).</summary>
internal static class UtcTime
{
    public static DateTime ToBson(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value.UtcDateTime
            : throw new ArgumentException($"Must be UTC (offset 00:00), but the offset is {value.Offset}.", nameof(value));

    public static DateTimeOffset FromBson(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? FromBson(DateTime? value) =>
        value is { } v ? FromBson(v) : null;
}
