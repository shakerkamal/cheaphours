using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace CheapHours.Providers.Kit.Testing;

/// <summary>
/// A vendor cloud on WireMock.Net for HTTP provider harnesses. It offers an OAuth 2.0 token endpoint
/// (<c>POST /oauth/token</c>, refresh-token grant, rotating refresh tokens, revocation), bearer checks on every
/// vendor path, failure and delay injection on every vendor path, and a request log. A harness adds its
/// vendor's endpoints with <see cref="Map"/>.
/// </summary>
public sealed class WireMockVendorFake : IDisposable
{
    /// <summary>The client id the fake accepts.</summary>
    public const string ClientId = "contract-client";

    /// <summary>The token endpoint's path.</summary>
    public const string TokenPath = "/oauth/token";

    private readonly Lock _lock = new();
    private readonly WireMockServer _server;
    private readonly bool _oauth;
    private readonly List<(string Method, Regex Path, Func<VendorRequest, Match, VendorAnswer> Handler)> _routes = [];
    private readonly List<VendorRequest> _vendorRequests = [];
    private readonly List<VendorRequest> _tokenRequests = [];
    private readonly HashSet<string> _validRefresh = [];
    private readonly HashSet<string> _validAccess = [];
    private readonly List<string> _secrets = [];
    private (int Status, TimeSpan? RetryAfter, string? Body)? _failure;
    private TimeSpan _delay;

    /// <summary>Starts the fake on a free local port.</summary>
    /// <param name="oauth">Whether vendor paths require a bearer token from the token endpoint.</param>
    public WireMockVendorFake(bool oauth)
    {
        _oauth = oauth;
        ClientSecret = "secret-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
        SeedRefreshToken = "seed-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
        _validRefresh.Add(SeedRefreshToken);
        _secrets.AddRange([ClientSecret, SeedRefreshToken]);
        _server = WireMockServer.Start();
        _server.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithCallback(HandleAsync));
    }

    /// <summary>Gets the fake's base address.</summary>
    public Uri BaseAddress => new(_server.Url! + "/");

    /// <summary>Gets the token endpoint.</summary>
    public Uri TokenEndpoint => new(BaseAddress, TokenPath.TrimStart('/'));

    /// <summary>Gets the client secret the fake accepts.</summary>
    public string ClientSecret { get; }

    /// <summary>Gets the seed refresh token the fake accepts once.</summary>
    public string SeedRefreshToken { get; }

    /// <summary>Gets the vendor requests received (not token requests).</summary>
    public IReadOnlyList<VendorRequest> VendorRequests
    {
        get
        {
            lock (_lock)
            {
                return [.. _vendorRequests];
            }
        }
    }

    /// <summary>Gets the token requests received.</summary>
    public IReadOnlyList<VendorRequest> TokenRequests
    {
        get
        {
            lock (_lock)
            {
                return [.. _tokenRequests];
            }
        }
    }

    /// <summary>Adds a vendor endpoint.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="pathPattern">A regular expression for the whole path, for example <c>^/api/v1/vehicles/(?&lt;id&gt;[^/]+)/state$</c>.</param>
    /// <param name="handler">Answers a matching request.</param>
    public void Map(HttpMethod method, string pathPattern, Func<VendorRequest, Match, VendorAnswer> handler)
    {
        ArgumentNullException.ThrowIfNull(method);
        lock (_lock)
        {
            _routes.Add((method.Method, new Regex(pathPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), handler));
        }
    }

    /// <summary>Makes every vendor path answer <paramref name="status"/> until <see cref="Recover"/>.</summary>
    /// <param name="status">The status.</param>
    /// <param name="retryAfter">A <c>Retry-After</c> in seconds, if any.</param>
    /// <param name="body">A JSON body, if any.</param>
    public void FailWith(int status, TimeSpan? retryAfter = null, string? body = null)
    {
        lock (_lock)
        {
            _failure = (status, retryAfter, body);
        }
    }

    /// <summary>Makes every vendor path wait before answering, until <see cref="Recover"/>.</summary>
    /// <param name="delay">The delay.</param>
    public void DelayBy(TimeSpan delay)
    {
        lock (_lock)
        {
            _delay = delay;
        }
    }

    /// <summary>Ends injected failures and delays.</summary>
    public void Recover()
    {
        lock (_lock)
        {
            _failure = null;
            _delay = TimeSpan.Zero;
        }
    }

    /// <summary>Makes the access tokens issued so far invalid; refreshing gives working ones.</summary>
    public void RejectCurrentAccessTokens()
    {
        lock (_lock)
        {
            _validAccess.Clear();
        }
    }

    /// <summary>Revokes every refresh and access token.</summary>
    public void RevokeAll()
    {
        lock (_lock)
        {
            _validAccess.Clear();
            _validRefresh.Clear();
        }
    }

    /// <summary>Returns every secret the fake issued or accepts: the client secret and all tokens.</summary>
    /// <returns>The secrets.</returns>
    public IReadOnlyList<string> AllSecrets()
    {
        lock (_lock)
        {
            return [.. _secrets];
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _server.Stop();

    private async Task<IResponseMessage> HandleAsync(IRequestMessage message)
    {
        var request = new VendorRequest(
            message.Method,
            message.Path,
            (message.Headers ?? new Dictionary<string, WireMockList<string>>())
                .ToDictionary(h => h.Key, h => h.Value.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            message.Body ?? string.Empty);

        if (request.Path == TokenPath)
        {
            lock (_lock)
            {
                _tokenRequests.Add(request);
            }

            return Answer(Token(request));
        }

        (int Status, TimeSpan? RetryAfter, string? Body)? failure;
        TimeSpan delay;
        lock (_lock)
        {
            _vendorRequests.Add(request);
            failure = _failure;
            delay = _delay;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }

        if (failure is { } f)
        {
            return Answer(new VendorAnswer(f.Status, f.Body), f.RetryAfter);
        }

        if (_oauth && !HasValidBearer(request))
        {
            return Answer(VendorAnswer.Raw(401, """{"error":"invalid_token"}"""));
        }

        foreach (var (method, path, handler) in Routes())
        {
            var match = path.Match(request.Path);
            if (method == request.Method && match.Success)
            {
                return Answer(handler(request, match));
            }
        }

        return Answer(VendorAnswer.Raw(404, """{"error":"not_found"}"""));
    }

    private (string, Regex, Func<VendorRequest, Match, VendorAnswer>)[] Routes()
    {
        lock (_lock)
        {
            return [.. _routes];
        }
    }

    private bool HasValidBearer(VendorRequest request)
    {
        var authorization = request.Header("Authorization");
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        lock (_lock)
        {
            return _validAccess.Contains(authorization["Bearer ".Length..]);
        }
    }

    private VendorAnswer Token(VendorRequest request)
    {
        var form = request.Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty, StringComparer.Ordinal);

        if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret)
        {
            return VendorAnswer.Raw(401, """{"error":"invalid_client"}""");
        }

        lock (_lock)
        {
            if (form.GetValueOrDefault("grant_type") != "refresh_token" || !_validRefresh.Remove(form.GetValueOrDefault("refresh_token") ?? string.Empty))
            {
                return VendorAnswer.Raw(400, """{"error":"invalid_grant"}""");
            }

            var access = "access-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
            var refresh = "refresh-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
            _validAccess.Add(access);
            _validRefresh.Add(refresh);
            _secrets.AddRange([access, refresh]);
            return VendorAnswer.Json(new Dictionary<string, object>
            {
                ["access_token"] = access,
                ["token_type"] = "Bearer",
                ["expires_in"] = 900,
                ["refresh_token"] = refresh,
            });
        }
    }

    private static ResponseMessage Answer(VendorAnswer answer, TimeSpan? retryAfter = null)
    {
        var headers = new Dictionary<string, WireMockList<string>>(StringComparer.OrdinalIgnoreCase);
        if (retryAfter is { } wait)
        {
            headers["Retry-After"] = new WireMockList<string>(((int)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        var response = new ResponseMessage { StatusCode = answer.Status, Headers = headers };
        if (answer.Body is not null)
        {
            headers["Content-Type"] = new WireMockList<string>("application/json");
            response.BodyData = new BodyData { BodyAsString = answer.Body, DetectedBodyType = BodyType.String, Encoding = Encoding.UTF8 };
        }

        return response;
    }
}
