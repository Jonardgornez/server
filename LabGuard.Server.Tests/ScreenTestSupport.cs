using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

/// <summary>A WebSocket that records what is sent and replays what a test queues. Sends can be made to stall, and receives can be fragmented.</summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly record struct Incoming(WebSocketMessageType Type, byte[] Payload, bool EndOfMessage);

    private readonly Channel<Incoming?> _incoming = Channel.CreateUnbounded<Incoming?>();
    private readonly List<(WebSocketMessageType Type, byte[] Payload)> _sent = [];
    private readonly object _sync = new();
    private readonly CancellationTokenSource _aborted = new();
    private (Incoming Message, int Offset)? _partial;
    private WebSocketState _state = WebSocketState.Open;

    public bool StallSends { get; set; }

    public bool Aborted { get; private set; }

    public IReadOnlyList<(WebSocketMessageType Type, byte[] Payload)> Sent
    {
        get
        {
            lock (_sync)
            {
                return _sent.ToArray();
            }
        }
    }

    public (WebSocketCloseStatus Status, string? Reason)? CloseSent { get; private set; }

    public void EnqueueMessage(WebSocketMessageType type, byte[] payload) => _incoming.Writer.TryWrite(new Incoming(type, payload, true));

    // A piece of a message that is not its last. The next queued piece completes it, and nothing more means the peer has stalled.
    public void EnqueueFragment(WebSocketMessageType type, byte[] payload) => _incoming.Writer.TryWrite(new Incoming(type, payload, false));

    public void EnqueueClose() => _incoming.Writer.TryWrite(null);

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => _state;

    public override void Abort()
    {
        lock (_sync)
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
        }

        _aborted.Cancel();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            CloseSent = (closeStatus, statusDescription);
            _state = WebSocketState.CloseSent;
        }

        return Task.CompletedTask;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
        if (_partial is null)
        {
            var next = await _incoming.Reader.ReadAsync(linked.Token);
            if (next is not { } message)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "");
            }

            _partial = (message, 0);
        }

        var (item, offset) = _partial.Value;
        var count = Math.Min(buffer.Count, item.Payload.Length - offset);
        Array.Copy(item.Payload, offset, buffer.Array!, buffer.Offset, count);
        var finished = offset + count == item.Payload.Length;
        _partial = finished ? null : (item, offset + count);
        return new WebSocketReceiveResult(count, item.Type, item.EndOfMessage && finished);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (StallSends)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        lock (_sync)
        {
            _sent.Add((messageType, buffer.ToArray()));
        }
    }

    public override void Dispose()
    {
    }
}

/// <summary>What a test read from a WebSocket: a whole message, or the close frame that ended it.</summary>
internal sealed record ReceivedMessage(WebSocketMessageType Type, byte[] Payload, WebSocketCloseStatus? CloseStatus, string? CloseReason)
{
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>Helpers that open the admin and PC sockets through TestHost, with the simulated connection headers the tests use.</summary>
internal static class LiveViewClient
{
    public const string LocalOrigin = "http://localhost:5173";
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly Regex StatusPattern = new(@"status code: (\d{3})", RegexOptions.CultureInvariant);

    /// <summary>The administrator's session cookie, as a Cookie request header value, from a fresh sign-in.</summary>
    public static async Task<string> SignInCookieAsync(TestApp app)
    {
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    public static Task<WebSocket> OpenAdminAsync(TestApp app, int pcId, string? cookie, string query = "", string? origin = LocalOrigin)
    {
        var socket = app.Server.CreateWebSocketClient();
        socket.ConfigureRequest = request =>
        {
            if (origin is not null)
            {
                request.Headers["Origin"] = origin;
            }

            if (cookie is not null)
            {
                request.Headers["Cookie"] = cookie;
            }
        };
        return socket.ConnectAsync(new Uri($"ws://localhost/api/pcs/{pcId}/screen/live{query}"), CancellationToken.None);
    }

    public static Task<WebSocket> OpenPcAsync(TestApp app, string token, string viewId, string remoteIp = "192.168.1.50")
    {
        var clientPort = app.Services.GetRequiredService<ServerPorts>().ClientPort.ToString();
        var socket = app.Server.CreateWebSocketClient();
        socket.ConfigureRequest = request =>
        {
            request.Headers["Authorization"] = $"Bearer {token}";
            request.Headers[SimulatedConnectionFilter.LocalPortHeader] = clientPort;
            request.Headers[SimulatedConnectionFilter.RemoteIpHeader] = remoteIp;
        };
        return socket.ConnectAsync(new Uri($"ws://localhost/api/client/screen/stream?viewId={viewId}"), CancellationToken.None);
    }

    /// <summary>Runs a connect that the server refuses, and returns the HTTP status the refusal carried.</summary>
    public static async Task<int> RefusedStatusAsync(Func<Task<WebSocket>> connect)
    {
        try
        {
            using var socket = await connect();
            Assert.Fail("The server accepted the WebSocket upgrade.");
            return 0;
        }
        catch (InvalidOperationException exception) when (StatusPattern.IsMatch(exception.Message))
        {
            return int.Parse(StatusPattern.Match(exception.Message).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static async Task<ReceivedMessage> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Patience);
        var chunk = new byte[64 * 1024];
        var buffer = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new ReceivedMessage(WebSocketMessageType.Close, [], result.CloseStatus, result.CloseStatusDescription);
            }

            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                return new ReceivedMessage(result.MessageType, buffer.ToArray(), null, null);
            }
        }
    }

    public static async Task<string> ReceiveStateAsync(WebSocket socket)
    {
        var message = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, message.Type);
        return message.Text;
    }

    public static Task SendAsync(WebSocket socket, byte[] payload, WebSocketMessageType type = WebSocketMessageType.Binary) =>
        socket.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);

    public static Task SendTextAsync(WebSocket socket, string text) =>
        SendAsync(socket, Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

    /// <summary>Bytes before a test frame's label: a baseline JPEG frame header that gives the size, 640 by 480 unless told otherwise.</summary>
    public const int FrameHeaderLength = 15;

    /// <summary>
    /// A JPEG-looking frame: a SOF0 header with the given size, then a label that tells frames apart, then random bytes.
    /// </summary>
    public static byte[] Frame(string label, int size = 64, int width = 640, int height = 480)
    {
        var frame = new byte[Math.Max(size, FrameHeaderLength + label.Length)];
        byte[] header =
        [
            0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            0x01, 0x01, 0x11, 0x00,
        ];
        header.CopyTo(frame, 0);
        Encoding.ASCII.GetBytes(label).CopyTo(frame, FrameHeaderLength);
        RandomNumberGenerator.Fill(frame.AsSpan(FrameHeaderLength + label.Length));
        return frame;
    }

    /// <summary>The label a test frame was built with, read back from a relayed frame.</summary>
    public static string LabelOf(byte[] frame, int length) => Encoding.ASCII.GetString(frame, FrameHeaderLength, length);

    public static async Task<(int Id, string Token)> AddPcAsync(TestApp app, string name = "PC-01")
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = app.Time.GetUtcNow();
        var id = await app.WithDbAsync(async db =>
        {
            var pc = new Pc { Name = name, TokenHash = hash, RegisteredAtUtc = now, LastSeenUtc = now };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
        return (id, token);
    }

    /// <summary>A heartbeat from the PC. Returns the decoded reply.</summary>
    public static async Task<JsonElement> HeartbeatAsync(TestApp app, string token, string remoteIp = "192.168.1.50")
    {
        using var pc = app.CreatePcClient(remoteIp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await pc.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
