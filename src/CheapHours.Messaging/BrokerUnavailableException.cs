namespace CheapHours.Messaging;

/// <summary>The broker cannot be reached or did not confirm a message. The message names only the broker's <c>host:port</c>.</summary>
public sealed class BrokerUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BrokerUnavailableException()
        : base("The message broker is not available.")
    {
    }

    /// <summary>Creates the exception for a broker endpoint.</summary>
    /// <param name="endpoint">The broker's <c>host:port</c>; never a connection string.</param>
    public BrokerUnavailableException(string endpoint)
        : base($"The message broker at {endpoint} is not available.")
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">A message without credentials.</param>
    /// <param name="innerException">The cause.</param>
    public BrokerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
