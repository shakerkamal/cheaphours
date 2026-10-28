using Microsoft.Extensions.Options;

namespace CheapHours.Messaging;

/// <summary>Checks <see cref="MessagingOptions"/> at startup, naming every problem at once.</summary>
public sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();

        if (options.RetryDelays.Any(d => d <= TimeSpan.Zero))
        {
            problems.Add("Messaging: every retry delay must be greater than zero.");
        }

        if (options.Prefetch is < 1 or > ushort.MaxValue)
        {
            problems.Add($"Messaging: Prefetch must be between 1 and {ushort.MaxValue}.");
        }

        if (options.BatchSize < 1 || options.BatchSize > options.Prefetch)
        {
            problems.Add("Messaging: BatchSize must be at least 1 and no larger than Prefetch.");
        }

        if (options.BatchWait <= TimeSpan.Zero)
        {
            problems.Add("Messaging: BatchWait must be greater than zero.");
        }

        if (options.PublishTimeout <= TimeSpan.Zero)
        {
            problems.Add("Messaging: PublishTimeout must be greater than zero.");
        }

        // RabbitMQ reserves names starting "amq."; spaces and control characters make names hard to manage.
        if (options.QueuePrefix.StartsWith("amq.", StringComparison.Ordinal) || options.QueuePrefix.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            problems.Add("Messaging: QueuePrefix must not start with 'amq.' or contain spaces.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
