namespace CheapHours.Messaging;

/// <summary>
/// Thrown by a batch handler when something it needs (the database) is down. The consumer then keeps the
/// batch unacknowledged and tries again with backoff, instead of spending the messages' retries.
/// </summary>
public sealed class DependencyUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DependencyUnavailableException()
        : base("A dependency the handler needs is not available.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is unavailable; no connection strings.</param>
    public DependencyUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">What is unavailable; no connection strings.</param>
    /// <param name="innerException">The cause.</param>
    public DependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
