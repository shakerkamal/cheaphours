using CheapHours.Host.Devices;

namespace CheapHours.UnitTests.Host;

public sealed class DeterministicGuidTests
{
    [Fact]
    public void The_same_name_gives_the_same_id()
    {
        Assert.Equal(DeterministicGuid.From("carcloud\nveh_1\n2026-10-28T18:00:00.0000000+00:00"), DeterministicGuid.From("carcloud\nveh_1\n2026-10-28T18:00:00.0000000+00:00"));
    }

    [Fact]
    public void Different_times_give_different_ids()
    {
        Assert.NotEqual(DeterministicGuid.From("carcloud\nveh_1\n2026-10-28T18:00:00.0000000+00:00"), DeterministicGuid.From("carcloud\nveh_1\n2026-10-28T18:01:00.0000000+00:00"));
        Assert.NotEqual(DeterministicGuid.From("carcloud\nveh_1\nx"), DeterministicGuid.From("carcloud\nveh_2\nx"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("carcloud\nveh_1\n2026-10-28T18:00:00Z")]
    [InlineData("a name with ümlauts and a long tail ................................................")]
    public void Version_and_variant_bits_mark_a_uuid_v8(string name)
    {
        var id = DeterministicGuid.From(name);

        Assert.Equal(8, id.Version);
        Assert.Equal('8', id.ToString()[14]);
        Assert.Contains(id.ToString()[19], "89ab");
    }
}
