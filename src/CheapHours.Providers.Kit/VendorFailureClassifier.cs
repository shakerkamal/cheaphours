using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CheapHours.Providers.Kit;

/// <summary>
/// The one place where vendor outcomes become failures (design §4). Reasons are fixed texts plus status
/// codes and, for refusals, the vendor's error code when it is a short <c>[a-z0-9_-]</c> word: the body itself
/// is never copied, so a secret the vendor echoes cannot leak.
/// </summary>
internal static partial class VendorFailureClassifier
{
    /// <summary>The most of a refusal body read to find its error code.</summary>
    private const int MaxBodyChars = 16 * 1024;

    /// <summary>Classifies a vendor response.</summary>
    /// <param name="response">The response.</param>
    /// <param name="time">The clock, for <c>Retry-After</c> dates.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns><see langword="null"/> for a 2xx status; otherwise the failure.</returns>
    public static async Task<VendorFailure?> ClassifyAsync(HttpResponseMessage response, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var code = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                new VendorFailure(VendorFailureKind.Transient, VendorFailure.VendorRateLimited, RetryAfterParser.From(response, time)),
            HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError =>
                new VendorFailure(VendorFailureKind.Transient, string.Create(CultureInfo.InvariantCulture, $"vendor returned {code}"), RetryAfterParser.From(response, time)),
            HttpStatusCode.Unauthorized =>
                new VendorFailure(VendorFailureKind.ReconnectNeeded, VendorFailure.ReconnectNeededReason),
            HttpStatusCode.NotFound =>
                new VendorFailure(VendorFailureKind.UnknownDevice, VendorFailure.UnknownDeviceReason),
            _ => await RefusalAsync(response, code, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Classifies an exception thrown by a vendor call.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="callerToken">The caller's token: a cancellation it asked for is not a vendor failure.</param>
    /// <returns>The failure, or <see langword="null"/> when the exception is not a vendor failure and must propagate.</returns>
    public static VendorFailure? Classify(Exception exception, CancellationToken callerToken) => exception switch
    {
        // Polly 8.4.2's exception carries no retry-after; ProviderHttpClient adds the break duration.
        BrokenCircuitException =>
            new VendorFailure(VendorFailureKind.Transient, VendorFailure.CircuitOpen),
        TimeoutRejectedException =>
            new VendorFailure(VendorFailureKind.Transient, VendorFailure.Timeout),
        OperationCanceledException when !callerToken.IsCancellationRequested =>
            new VendorFailure(VendorFailureKind.Transient, VendorFailure.Timeout),
        HttpRequestException =>
            new VendorFailure(VendorFailureKind.Transient, VendorFailure.Unreachable),
        _ => null,
    };

    /// <summary>Extracts a vendor error code from a body: its <c>error</c> or <c>code</c> field when it is a short safe word.</summary>
    /// <param name="body">The body text.</param>
    /// <returns>The code, or <see langword="null"/> when there is no usable one.</returns>
    public static string? ErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in (ReadOnlySpan<string>)["error", "code"])
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text
                    && SafeCode().IsMatch(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static async Task<VendorFailure> RefusalAsync(HttpResponseMessage response, int code, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var error = ErrorCode(body.Length > MaxBodyChars ? null : body);
        var reason = error is null
            ? string.Create(CultureInfo.InvariantCulture, $"vendor refused ({code})")
            : string.Create(CultureInfo.InvariantCulture, $"vendor refused ({code}): {error}");
        return new VendorFailure(VendorFailureKind.Rejected, reason);
    }

    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCode();
}
