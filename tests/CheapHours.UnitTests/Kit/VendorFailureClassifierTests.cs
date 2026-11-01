using System.Net;
using System.Text;
using CheapHours.Core.Commands;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CheapHours.UnitTests.Kit;

/// <summary>Every row of the classification table in design §4.</summary>
public sealed class VendorFailureClassifierTests
{
    private static readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero));

    private static async Task<VendorFailure?> ClassifyAsync(HttpStatusCode status, string? body = null, string? retryAfter = null)
    {
        using var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return await VendorFailureClassifier.ClassifyAsync(response, Time, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Success_is_no_failure(HttpStatusCode status)
    {
        Assert.Null(await ClassifyAsync(status));
    }

    [Fact]
    public async Task Too_many_requests_is_transient_with_retry_after()
    {
        var failure = await ClassifyAsync(HttpStatusCode.TooManyRequests, retryAfter: "30");

        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, "vendor-rate-limited", TimeSpan.FromSeconds(30)), failure);
        Assert.True(failure!.AffectsAccount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "vendor returned 500")]
    [InlineData(HttpStatusCode.BadGateway, "vendor returned 502")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "vendor returned 503")]
    [InlineData(HttpStatusCode.RequestTimeout, "vendor returned 408")]
    public async Task Server_errors_and_408_are_transient(HttpStatusCode status, string reason)
    {
        var failure = await ClassifyAsync(status);

        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, reason), failure);
        Assert.False(failure!.AffectsAccount);
    }

    [Fact]
    public async Task Server_error_carries_retry_after_when_present()
    {
        var failure = await ClassifyAsync(HttpStatusCode.ServiceUnavailable, retryAfter: "Wed, 28 Oct 2026 18:00:45 GMT");

        Assert.Equal(TimeSpan.FromSeconds(45), failure!.RetryAfter);
    }

    [Fact]
    public async Task Unauthorized_is_reconnect_needed()
    {
        Assert.Equal(new VendorFailure(VendorFailureKind.ReconnectNeeded, "reconnect-needed"), await ClassifyAsync(HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Not_found_is_unknown_device()
    {
        Assert.Equal(new VendorFailure(VendorFailureKind.UnknownDevice, "unknown-device"), await ClassifyAsync(HttpStatusCode.NotFound));
    }

    [Theory]
    [InlineData("""{"error":"invalid_mode"}""", "vendor refused (400): invalid_mode")]
    [InlineData("""{"code":"bad-request-2"}""", "vendor refused (400): bad-request-2")]
    [InlineData("""{"message":"nope"}""", "vendor refused (400)")]
    [InlineData("not json", "vendor refused (400)")]
    [InlineData("""["invalid_mode"]""", "vendor refused (400)")]
    [InlineData("""{"error":"Has Spaces And Caps"}""", "vendor refused (400)")]
    public async Task Other_client_errors_are_rejected_with_a_safe_vendor_code(string body, string reason)
    {
        Assert.Equal(new VendorFailure(VendorFailureKind.Rejected, reason), await ClassifyAsync(HttpStatusCode.BadRequest, body));
    }

    [Fact]
    public async Task Unprocessable_entity_is_rejected()
    {
        var failure = await ClassifyAsync(HttpStatusCode.UnprocessableEntity, """{"error":"not_plugged_in"}""");

        Assert.Equal(VendorFailureKind.Rejected, failure!.Kind);
        Assert.Contains("not_plugged_in", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_echoed_secret_never_reaches_the_reason()
    {
        const string Secret = "s3cr3t-client-secret-value";
        var body = $$"""{"error":"{{Secret}} in client_secret={{Secret}}","request":"client_secret={{Secret}}&refresh_token={{Secret}}"}""";

        var failure = await ClassifyAsync(HttpStatusCode.BadRequest, body);

        Assert.DoesNotContain(Secret, failure!.Reason, StringComparison.Ordinal);
        Assert.Equal("vendor refused (400)", failure.Reason);
    }

    [Fact]
    public async Task An_echoed_secret_in_a_server_error_never_reaches_the_reason()
    {
        var failure = await ClassifyAsync(HttpStatusCode.InternalServerError, """{"error":"refresh_token=abc123"}""");

        Assert.Equal("vendor returned 500", failure!.Reason);
    }

    [Fact]
    public void Attempt_timeout_is_transient()
    {
        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, "vendor-timeout"), VendorFailureClassifier.Classify(new TimeoutRejectedException(), CancellationToken.None));
    }

    [Fact]
    public void Cancellation_not_asked_for_by_the_caller_is_a_timeout()
    {
        Assert.Equal("vendor-timeout", VendorFailureClassifier.Classify(new TaskCanceledException(), CancellationToken.None)!.Reason);
    }

    [Fact]
    public void Cancellation_asked_for_by_the_caller_is_not_a_vendor_failure()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Null(VendorFailureClassifier.Classify(new OperationCanceledException(cancelled.Token), cancelled.Token));
    }

    [Fact]
    public void Connection_failure_is_transient()
    {
        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, "vendor-unreachable"), VendorFailureClassifier.Classify(new HttpRequestException("refused"), CancellationToken.None));
    }

    [Fact]
    public void Open_circuit_is_transient()
    {
        var failure = VendorFailureClassifier.Classify(new BrokenCircuitException("open"), CancellationToken.None);

        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, "circuit-open"), failure);
        Assert.True(failure!.AffectsAccount);
    }

    [Fact]
    public void Other_exceptions_propagate()
    {
        Assert.Null(VendorFailureClassifier.Classify(new InvalidOperationException("bug"), CancellationToken.None));
    }

    [Fact]
    public void Failures_map_to_send_and_poll_results()
    {
        var transient = new VendorFailure(VendorFailureKind.Transient, "vendor-rate-limited", TimeSpan.FromSeconds(30));

        Assert.Equal(new SendResult.Transient("vendor-rate-limited", TimeSpan.FromSeconds(30)), transient.ToSendResult());
        Assert.Equal(new SendResult.Rejected("unknown-device"), new VendorFailure(VendorFailureKind.UnknownDevice, "unknown-device").ToSendResult());
        Assert.Equal(new SendResult.Rejected("vendor refused (422): not_plugged_in"), new VendorFailure(VendorFailureKind.Rejected, "vendor refused (422): not_plugged_in").ToSendResult());
        Assert.Equal(new SendResult.Rejected("reconnect-needed"), new VendorFailure(VendorFailureKind.ReconnectNeeded, "reconnect-needed").ToSendResult());
        var failed = transient.ToPollFailed();
        Assert.Equal("vendor-rate-limited", failed.Reason);
        Assert.Equal(TimeSpan.FromSeconds(30), failed.RetryAfter);
    }
}
