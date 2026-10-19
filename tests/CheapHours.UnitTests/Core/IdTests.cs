using CheapHours.Core.Ids;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Core;

public sealed class IdTests
{
    [Fact]
    public void Valid_id_parses()
    {
        var id = DeviceId.Parse("dev_wallbox_01");

        Assert.Equal("dev_wallbox_01", id.ToString());
        Assert.Equal("dev_wallbox_01", id.Value);
    }

    [Fact]
    public void Wrong_prefix_is_refused_with_the_expected_prefix_named()
    {
        Assert.False(DeviceId.TryParse("plan_8812", out _));

        var ex = Assert.Throws<FormatException>(() => DeviceId.Parse("plan_8812"));
        Assert.Contains("'dev_'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev_")]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("dev_has space")]
    [InlineData("dev_ümlaut")]
    [InlineData("DEV_upper_prefix")]
    public void Malformed_ids_are_refused(string candidate)
    {
        Assert.False(DeviceId.TryParse(candidate, out _));
    }

    [Fact]
    public void Null_is_refused()
    {
        Assert.False(DeviceId.TryParse(null, out _));
    }

    [Fact]
    public void Body_longer_than_the_maximum_is_refused()
    {
        Assert.True(DeviceId.TryParse("dev_" + new string('a', PrefixedId.MaxBodyLength), out _));
        Assert.False(DeviceId.TryParse("dev_" + new string('a', PrefixedId.MaxBodyLength + 1), out _));
    }

    [Fact]
    public void Every_id_kind_has_its_own_prefix_and_refuses_the_others()
    {
        string[] prefixes = [DeviceId.Prefix, GoalId.Prefix, PlanId.Prefix, CommandId.Prefix, OwnerId.Prefix];
        Assert.Equal(["dev_", "goal_", "plan_", "cmd_", "own_"], prefixes);

        AssertAcceptsOnlyOwnPrefix<DeviceId>(prefixes);
        AssertAcceptsOnlyOwnPrefix<GoalId>(prefixes);
        AssertAcceptsOnlyOwnPrefix<PlanId>(prefixes);
        AssertAcceptsOnlyOwnPrefix<CommandId>(prefixes);
        AssertAcceptsOnlyOwnPrefix<OwnerId>(prefixes);
    }

    [Fact]
    public void Equal_values_are_equal_ids()
    {
        Assert.Equal(PlanId.Parse("plan_8812"), PlanId.Parse("plan_8812"));
        Assert.NotEqual(PlanId.Parse("plan_8812"), PlanId.Parse("plan_8813"));
    }

    [Fact]
    public void New_ids_have_the_prefix_a_26_character_body_and_parse_back()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero));

        var id = CommandId.New(time);

        Assert.StartsWith("cmd_", id.Value, StringComparison.Ordinal);
        Assert.Equal(4 + 26, id.Value.Length);
        Assert.Equal(id, CommandId.Parse(id.Value));
    }

    [Fact]
    public void New_ids_sort_by_creation_time()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero));

        var earlier = CommandId.New(time);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var later = CommandId.New(time);

        Assert.True(string.CompareOrdinal(earlier.Value, later.Value) < 0, $"{earlier} should sort before {later}");
    }

    [Fact]
    public void Default_id_has_no_value()
    {
        var id = default(DeviceId);

        Assert.Throws<InvalidOperationException>(() => id.Value);
        Assert.Equal(string.Empty, id.ToString());
    }

    [Fact]
    public void No_implicit_conversion_exists_between_ids_or_from_string()
    {
        var idTypes = typeof(DeviceId).Assembly.GetTypes()
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPrefixedId<>)))
            .ToArray();

        Assert.Equal(5, idTypes.Length);
        Assert.All(idTypes, t =>
            Assert.DoesNotContain(t.GetMethods(), m => m.Name is "op_Implicit"));
    }

    private static void AssertAcceptsOnlyOwnPrefix<TId>(string[] prefixes)
        where TId : struct, IPrefixedId<TId>
    {
        foreach (var prefix in prefixes)
        {
            var accepted = TId.TryParse(prefix + "abc", out _);
            Assert.True(accepted == (prefix == TId.Prefix), $"{typeof(TId).Name} accepted '{prefix}abc': {accepted}");
        }
    }
}
