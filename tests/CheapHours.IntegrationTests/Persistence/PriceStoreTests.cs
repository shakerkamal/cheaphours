using CheapHours.Core.Prices;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Prices;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>The <c>prices</c> and <c>priceDays</c> collections (price-import spec, design §3). Each test uses its own zone.</summary>
public sealed class PriceStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly DateTimeOffset DayStart = new(2026, 11, 1, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 1, 12, 5, 0, TimeSpan.Zero);

    private readonly string _zone = "Z" + SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private PriceStore Prices => new(_database);

    private PriceDayStore Days => new(_database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await SharedDatabases.GetAsync(mongo, nameof(PriceStoreTests));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Identical_reimport_modifies_nothing_and_stores_each_slot_once()
    {
        var slots = Slots(96, i => 80m + i);

        var first = await Prices.UpsertAsync(slots, ImportedAt, Ct);
        var second = await Prices.UpsertAsync(slots, ImportedAt.AddHours(1), Ct);

        Assert.Equal(new PriceWriteResult(96, 0), first);
        Assert.Equal(new PriceWriteResult(0, 0), second);
        Assert.Equal(96, await Prices.CountAsync(_zone, DayStart, DayStart.AddDays(1), Ct));
        var raw = _database.GetCollection<BsonDocument>(MongoIndexes.Prices);
        Assert.Equal(96, await raw.CountDocumentsAsync(new BsonDocument("zone", _zone), cancellationToken: Ct));
    }

    [Fact]
    public async Task Changed_price_is_updated_and_keeps_its_first_import_time()
    {
        await Prices.UpsertAsync(Slots(4, _ => 80m), ImportedAt, Ct);

        var result = await Prices.UpsertAsync([new PriceSlot(_zone, DayStart.AddMinutes(15), -4.2m)], ImportedAt.AddHours(1), Ct);

        Assert.Equal(new PriceWriteResult(0, 1), result);
        var raw = await _database.GetCollection<BsonDocument>(MongoIndexes.Prices)
            .Find(new BsonDocument("_id", $"{_zone}|2026-11-01T23:15:00Z")).SingleAsync(Ct);
        Assert.Equal(new Decimal128(-4.2m), raw["priceEurPerMwh"].AsDecimal128);
        Assert.Equal(ImportedAt.UtcDateTime, raw["importedAt"].ToUniversalTime());
        Assert.Equal(1, raw["schemaVersion"].AsInt32);
    }

    [Fact]
    public async Task Range_read_returns_one_zone_in_time_order()
    {
        // Written out of order, with another zone's slot in the same range.
        await Prices.UpsertAsync([.. Slots(8, i => 100m - i).Reverse()], ImportedAt, Ct);
        await Prices.UpsertAsync([new PriceSlot(_zone + "X", DayStart, 50m)], ImportedAt, Ct);

        var read = await Prices.ReadAsync(_zone, DayStart.AddMinutes(30), DayStart.AddMinutes(90), afterUtc: null, limit: 0, Ct);

        Assert.Equal([DayStart.AddMinutes(30), DayStart.AddMinutes(45), DayStart.AddMinutes(60), DayStart.AddMinutes(75)], read.Select(s => s.StartUtc));
        Assert.Equal([98m, 97m, 96m, 95m], read.Select(s => s.PriceEurPerMwh));
        Assert.All(read, s => Assert.Equal(_zone, s.Zone));
    }

    [Fact]
    public async Task Range_read_pages_after_a_cursor()
    {
        await Prices.UpsertAsync(Slots(10, _ => 80m), ImportedAt, Ct);

        var first = await Prices.ReadAsync(_zone, DayStart, DayStart.AddDays(1), afterUtc: null, limit: 4, Ct);
        var second = await Prices.ReadAsync(_zone, DayStart, DayStart.AddDays(1), afterUtc: first[^1].StartUtc, limit: 4, Ct);

        Assert.Equal(DayStart.AddMinutes(45), first[^1].StartUtc);
        Assert.Equal(DayStart.AddMinutes(60), second[0].StartUtc);
        Assert.Equal(4, second.Count);
    }

    [Fact]
    public async Task Partial_day_stays_incomplete()
    {
        var state = await Days.RecordImportAsync(_zone, Day, slotCount: 96, complete: false, ImportedAt, Ct);

        Assert.False(state.IsComplete);
        Assert.Null(state.AnnouncedAt);
        Assert.Equal(96, state.SlotCount);
        Assert.Equal(Day, state.Day);
    }

    [Fact]
    public async Task Completion_and_announcement_are_marked_once()
    {
        await Days.RecordImportAsync(_zone, Day, 96, complete: false, ImportedAt, Ct);
        var complete = await Days.RecordImportAsync(_zone, Day, 96, complete: true, ImportedAt.AddHours(1), Ct);
        var again = await Days.RecordImportAsync(_zone, Day, 96, complete: true, ImportedAt.AddHours(2), Ct);

        Assert.Equal(ImportedAt.AddHours(1), complete.CompleteAt);
        Assert.Equal(ImportedAt.AddHours(1), again.CompleteAt);

        await Days.MarkAnnouncedAsync(_zone, Day, ImportedAt.AddHours(1), Ct);
        await Days.MarkAnnouncedAsync(_zone, Day, ImportedAt.AddHours(3), Ct);

        Assert.Equal(ImportedAt.AddHours(1), (await Days.GetAsync(_zone, Day, Ct))!.AnnouncedAt);
    }

    [Fact]
    public async Task The_first_attempt_is_marked_once()
    {
        await Days.MarkAttemptedAsync(_zone, Day, 96, ImportedAt, Ct);
        await Days.MarkAttemptedAsync(_zone, Day, 96, ImportedAt.AddHours(1), Ct);

        var state = await Days.GetAsync(_zone, Day, Ct);
        Assert.Equal(ImportedAt, state!.AttemptedAt);
        Assert.False(state.IsComplete);
        Assert.Null(state.GaveUpAt);
    }

    [Fact]
    public async Task Giving_up_is_marked_once_even_for_a_day_never_imported()
    {
        Assert.Null(await Days.GetAsync(_zone, Day, Ct));

        Assert.True(await Days.MarkGaveUpAsync(_zone, Day, 96, ImportedAt, Ct));
        Assert.False(await Days.MarkGaveUpAsync(_zone, Day, 96, ImportedAt.AddHours(1), Ct));

        var state = await Days.GetAsync(_zone, Day, Ct);
        Assert.Equal(ImportedAt, state!.GaveUpAt);
        Assert.False(state.IsComplete);
    }

    private PriceSlot[] Slots(int count, Func<int, decimal> price) =>
        [.. Enumerable.Range(0, count).Select(i => new PriceSlot(_zone, DayStart.AddMinutes(15 * i), price(i)))];
}
