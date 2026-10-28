using CheapHours.Core.Ids;
using CheapHours.Host.Api.Auth;
using CheapHours.Persistence.ApiKeys;
using Microsoft.Extensions.Logging;

namespace CheapHours.UnitTests.Host;

public sealed class MongoApiKeyStoreTests
{
    private static readonly string Hash = new('a', 64);

    private readonly ListLogger _logger = new();

    private async Task<ApiKeyRecord?> FindAsync(StoredApiKey? stored) =>
        await new MongoApiKeyStore(new OneKey(stored), _logger).FindByHashAsync(Hash, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Valid_stored_key_becomes_a_record()
    {
        var record = await FindAsync(new StoredApiKey(Hash, "demo", "own_demo", ["read", "execute"]));

        Assert.NotNull(record);
        Assert.Equal("demo", record.Name);
        Assert.Equal(OwnerId.Parse("own_demo"), record.OwnerId);
        Assert.True(record.Scopes.SetEquals(["read", "execute"]));
        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public async Task Unknown_hash_is_null_without_a_log()
    {
        Assert.Null(await FindAsync(null));
        Assert.Empty(_logger.Lines);
    }

    [Theory]
    [InlineData("not-a-hash", "own_demo", "read", "hash")]
    [InlineData("HASH", "own_demo", "read,admin", "admin")]
    [InlineData("HASH", "own_demo", "execute", "read")]
    [InlineData("HASH", "demo", "read", "owner")]
    public async Task Stored_key_breaking_a_rule_is_refused_and_logged_by_name(string hash, string owner, string scopes, string rule)
    {
        var storedHash = hash == "HASH" ? Hash : hash;

        var record = await FindAsync(new StoredApiKey(storedHash, "broken-key", owner, scopes.Split(',')));

        Assert.Null(record);
        var line = Assert.Single(_logger.Lines);
        Assert.Contains("broken-key", line, StringComparison.Ordinal);
        Assert.Contains(rule, line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Hash, line, StringComparison.Ordinal);
    }

    private sealed class OneKey(StoredApiKey? key) : IStoredApiKeys
    {
        public Task<StoredApiKey?> FindByHashAsync(string keyHash, CancellationToken cancellationToken) => Task.FromResult(key);

        public Task SeedAsync(StoredApiKey key, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ListLogger : ILogger<MongoApiKeyStore>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
