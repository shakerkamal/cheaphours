using System.Collections.Concurrent;
using CheapHours.Persistence.ChargePoints;

namespace CheapHours.UnitTests.Host;

/// <summary>A faithful in-memory registrations store for unit tests (same seeding and revocation rules as MongoDB's).</summary>
internal sealed class InMemoryChargePointRegistrations : IChargePointRegistrations
{
    private readonly ConcurrentDictionary<string, ChargePointRegistration> _byId = new(StringComparer.Ordinal);

    public int Lookups { get; private set; }

    public Task<ChargePointRegistration?> FindAsync(string chargePointId, CancellationToken cancellationToken)
    {
        Lookups++;
        return Task.FromResult(_byId.GetValueOrDefault(chargePointId));
    }

    public Task SeedAsync(ChargePointRegistration registration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _byId.AddOrUpdate(
            registration.ChargePointId,
            _ => registration with { CreatedAt = now, RevokedAt = null },
            (_, existing) => registration with { CreatedAt = existing.CreatedAt, RevokedAt = existing.RevokedAt });
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string chargePointId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (_byId.TryGetValue(chargePointId, out var existing))
        {
            _byId[chargePointId] = existing with { RevokedAt = at };
        }

        return Task.CompletedTask;
    }
}
