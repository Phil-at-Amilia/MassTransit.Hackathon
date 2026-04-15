using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MassTransit.Hackathon.Dashboard;

internal sealed record QueueStats(
    [property: JsonPropertyName("name")]                     string Name,
    [property: JsonPropertyName("messages_ready")]           int    MessagesReady,
    [property: JsonPropertyName("messages_unacknowledged")]  int    MessagesUnacknowledged,
    [property: JsonPropertyName("messages")]                 int    TotalMessages,
    [property: JsonPropertyName("consumers")]                int    Consumers);

/// <summary>
/// Polls the RabbitMQ Management HTTP API every two seconds to retrieve queue statistics.
/// Thread-safe: <see cref="Latest"/> is written atomically via reference assignment.
/// </summary>
internal sealed class RabbitMqMonitor
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private volatile QueueStats[] _latest = [];

    public QueueStats[] Latest => _latest;
    public bool IsConnected { get; private set; }

    public RabbitMqMonitor()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:15672"),
            Timeout     = TimeSpan.FromSeconds(3),
        };
        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes("guest:guest"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credential);
    }

    public async Task StartPollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await _http.GetAsync("/api/queues", ct);
                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    var queues = await JsonSerializer.DeserializeAsync<QueueStats[]>(stream, JsonOpts, ct);
                    if (queues is not null)
                        _latest = queues;
                    IsConnected = true;
                }
                else
                {
                    IsConnected = false;
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                IsConnected = false;
            }

            await Task.Delay(2_000, ct).ContinueWith(_ => { });
        }
    }
}
