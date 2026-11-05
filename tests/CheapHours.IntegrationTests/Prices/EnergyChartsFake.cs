using System.Collections.Concurrent;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CheapHours.IntegrationTests.Prices;

/// <summary>
/// A stand-in for the Energy-Charts <c>/price</c> endpoint on WireMock. Each day answers what the test scripted
/// (a recorded fixture, a status, a delay), in order, repeating the last answer; unscripted days get 404 with
/// <c>no content available</c>, as the real source does for an unpublished day. No test reaches the real API.
/// </summary>
internal sealed class EnergyChartsFake : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly ConcurrentDictionary<string, Queue<Answer>> _answers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _requests = new();

    public EnergyChartsFake()
    {
        _server.Given(Request.Create().WithPath("/price").UsingGet()).RespondWith(Response.Create().WithCallback(request =>
        {
            var day = request.Query?.TryGetValue("start", out var values) == true ? values.ToString() : string.Empty;
            _requests.Enqueue(day);
            var answer = Next(day);
            if (answer.Delay > TimeSpan.Zero)
            {
                Thread.Sleep(answer.Delay);
            }

            var response = new WireMock.ResponseMessage
            {
                StatusCode = answer.Status,
                BodyData = new WireMock.Util.BodyData { BodyAsString = answer.Body, DetectedBodyType = WireMock.Types.BodyType.String },
            };
            response.AddHeader("Content-Type", answer.Status == 200 ? "application/json" : "text/plain");
            return response;
        }));
    }

    /// <summary>Gets the base URL to configure as <c>Prices:BaseUrl</c>.</summary>
    public Uri BaseUrl => new(_server.Urls[0] + "/");

    /// <summary>Gets the requested days (the <c>start</c> query value), in order.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>Returns the recorded response for <paramref name="day"/> (2026-03-29, 2026-10-25 or 2026-11-02).</summary>
    /// <param name="day">A recorded day.</param>
    /// <returns>The response body as the source returned it.</returns>
    public static string Fixture(DateOnly day) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prices", "Fixtures", $"energy-charts-de-lu-{day:yyyy-MM-dd}.json"));

    /// <summary>Scripts the answers for <paramref name="day"/>, in order; the last one repeats.</summary>
    /// <param name="day">The Berlin day.</param>
    /// <param name="answers">The answers.</param>
    public void Script(DateOnly day, params Answer[] answers) =>
        _answers[day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)] = new Queue<Answer>(answers);

    public void Dispose() => _server.Stop();

    private Answer Next(string day)
    {
        if (!_answers.TryGetValue(day, out var queue))
        {
            return Answer.NotPublished;
        }

        lock (queue)
        {
            return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }
    }

    /// <summary>One scripted answer.</summary>
    /// <param name="Status">The HTTP status.</param>
    /// <param name="Body">The body.</param>
    /// <param name="Delay">How long to wait before answering.</param>
    public sealed record Answer(int Status, string Body, TimeSpan Delay = default)
    {
        public static Answer NotPublished { get; } = new(404, "no content available");

        public static Answer Ok(string body) => new(200, body);
    }
}
