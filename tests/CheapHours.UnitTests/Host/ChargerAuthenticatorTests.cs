using System.Security.Cryptography;
using System.Text;
using CheapHours.Core.Ids;
using CheapHours.Host.Gateway;
using CheapHours.Persistence.ChargePoints;

namespace CheapHours.UnitTests.Host;

public sealed class ChargerAuthenticatorTests
{
    private const string Password = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryChargePointRegistrations _registrations = new();
    private readonly ChargerAuthenticator _authenticator;

    public ChargerAuthenticatorTests()
    {
        _authenticator = new ChargerAuthenticator(_registrations);
        _registrations.SeedAsync(new ChargePointRegistration("CP001", OwnerId.Parse("own_demo"), Hash(Password), 11000), T0, CancellationToken.None).GetAwaiter().GetResult();
        _registrations.SeedAsync(new ChargePointRegistration("CP002", OwnerId.Parse("own_demo"), Hash("other"), 11000), T0, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Hash(string password) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    private static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

    private Task<ChargerAuthResult> Authenticate(string pathId, string? header) =>
        _authenticator.AuthenticateAsync(pathId, header, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Correct_credentials_return_the_registration()
    {
        var result = await Authenticate("CP001", Basic("CP001", Password));

        Assert.True(result.Succeeded);
        Assert.Equal("CP001", result.Registration!.ChargePointId);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Upper_case_stored_hash_matches()
    {
        await _registrations.SeedAsync(new ChargePointRegistration("CP003", OwnerId.Parse("own_demo"), Hash(Password).ToUpperInvariant(), 11000), T0, CancellationToken.None);

        Assert.True((await Authenticate("CP003", Basic("CP003", Password))).Succeeded);
    }

    [Fact]
    public async Task Wrong_password_fails()
    {
        Assert.Equal(ChargerAuthenticator.WrongPassword, (await Authenticate("CP001", Basic("CP001", "nope"))).FailureReason);
    }

    [Fact]
    public async Task Unknown_charger_fails()
    {
        Assert.Equal(ChargerAuthenticator.Unknown, (await Authenticate("CP999", Basic("CP999", Password))).FailureReason);
    }

    [Fact]
    public async Task Username_must_match_the_path()
    {
        Assert.Equal(ChargerAuthenticator.Mismatch, (await Authenticate("CP001", Basic("CP002", "other"))).FailureReason);
    }

    [Fact]
    public async Task Revoked_registration_fails_even_with_the_right_password()
    {
        await _registrations.RevokeAsync("CP001", T0.AddHours(1), CancellationToken.None);

        Assert.Equal(ChargerAuthenticator.Revoked, (await Authenticate("CP001", Basic("CP001", Password))).FailureReason);
    }

    [Fact]
    public async Task Missing_header_fails()
    {
        Assert.Equal(BasicCredentials.Missing, (await Authenticate("CP001", null)).FailureReason);
    }

    [Fact]
    public async Task Malformed_header_fails()
    {
        Assert.Equal(BasicCredentials.Malformed, (await Authenticate("CP001", "Basic ???")).FailureReason);
    }

    [Fact]
    public async Task Unknown_and_mismatched_ids_still_do_the_lookup()
    {
        await Authenticate("CP999", Basic("CP999", Password));
        await Authenticate("CP001", Basic("CP002", "other"));

        Assert.Equal(2, _registrations.Lookups);
    }
}
