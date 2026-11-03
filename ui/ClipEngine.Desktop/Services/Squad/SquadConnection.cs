using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClipEngine.Desktop.Services.Squad;

// The live side of the squad: one WebSocket to the squad's service, kept open and reconnected
// with growing waits while the app runs.
internal interface ISquadChannel : IDisposable
{
    // Called on a background thread.
    event Action<JsonObject>? Received;
    event Action<bool>? ConnectedChanged;
    // The service turned this member away (removed from the squad, or the squad is gone).
    event Action? Rejected;

    bool Connected { get; }
    void Start();
    void Send(JsonObject message);
}

internal sealed class SquadConnection(string server, string squad, string token) : ISquadChannel
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private Task? _loop;

    public event Action<JsonObject>? Received;
    public event Action<bool>? ConnectedChanged;
    public event Action? Rejected;

    public bool Connected { get; private set; }

    public void Start() => _loop ??= Task.Run(RunAsync);

    public void Send(JsonObject message)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        _ = Task.Run(async () =>
        {
            await _sendGate.WaitAsync(_stop.Token);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                _sendGate.Release();
            }
        });
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        var url = new Uri(server.Replace("https://", "wss://").Replace("http://", "ws://") + $"/v1/squads/{squad}/socket");
        while (!_stop.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            try
            {
                await socket.ConnectAsync(url, _stop.Token);
                _socket = socket;
                attempt = 0;
                SetConnected(true);
                await ReceiveAsync(socket);
            }
            catch (WebSocketException e) when (e.Message.Contains("401") || e.Message.Contains("404"))
            {
                Rejected?.Invoke();
                return;
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or HttpRequestException)
            {
            }
            finally
            {
                _socket = null;
                SetConnected(false);
            }
            if (socket.CloseStatus is (WebSocketCloseStatus)4001)
            {
                Rejected?.Invoke();
                return;
            }
            if (_stop.IsCancellationRequested) return;
            try
            {
                await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, _stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 1_000_000) return;  // nothing the service sends is this big
            if (!result.EndOfMessage) continue;
            try
            {
                if (JsonNode.Parse(message.ToArray()) is JsonObject json) Received?.Invoke(json);
            }
            catch (System.Text.Json.JsonException)
            {
            }
            message.SetLength(0);
        }
    }

    private void SetConnected(bool connected)
    {
        if (Connected == connected) return;
        Connected = connected;
        ConnectedChanged?.Invoke(connected);
    }

    public void Dispose()
    {
        _stop.Cancel();
        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(1000);
            }
            catch (Exception e) when (e is AggregateException or WebSocketException or ObjectDisposedException)
            {
            }
        }
    }
}
