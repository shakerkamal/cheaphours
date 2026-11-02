using Xunit.Sdk;

namespace CheapHours.ContractTests;

/// <summary>Checks that a contract fact skips rather than passes or fails.</summary>
internal static class Skips
{
    /// <summary>Runs a fact and returns its skip reason; fails when it passes or fails instead of skipping.</summary>
    /// <param name="fact">The fact to run.</param>
    /// <returns>The skip reason.</returns>
    public static async Task<string> ReasonOfAsync(Func<Task> fact)
    {
        // Caught by hand: xUnit's Assert.Throws* let a skip through, which would skip this test too.
        Exception? thrown = null;
        try
        {
            await fact();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        var outcome = thrown is null ? "passed" : $"{thrown.GetType().Name}: {thrown.Message.Replace("$XunitDynamicSkip$", "<skip>", StringComparison.Ordinal)}";
        Assert.True(thrown is SkipException, $"Expected the fact to be skipped, but it {outcome}.");
        return thrown.Message;
    }
}
