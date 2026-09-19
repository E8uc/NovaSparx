using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NovaSparx.Backend;

/// <summary>
/// Persistent reverse WebSocket client used by NovaSparx AutoLink.
///
/// NovaSparx connects OUT to the stable Cloudflare endpoint, so Back4App does
/// not need a stable inbound hostname for FNAA to reach heavy asset parsing.
///
/// Environment:
///   NOVASPARX_LINK_URL   = wss://.../connect
///   NOVASPARX_SHARED_TOKEN = canonical shared secret (recommended)
///   NOVASPARX_LINK_TOKEN = legacy route-specific shared secret
///
/// Protocol:
///   Worker -> Nova:
///     {"type":"request","id":"...","method":"GET",
///      "path":"/v1/resolve","query":{"path":"/Game/..."}}
///     {"type":"cancel","id":"...","reason":"client-request-aborted"}
///
///   Nova -> Worker:
///     {"type":"response","id":"...","status":200,
///      "contentType":"application/json; charset=utf-8",
///      "length":1234,"chunks":1}
///     <binary body chunk(s), max 512 KiB each>
///     {"type":"response_end","id":"..."}
///
/// Requests are intentionally processed sequentially on one socket. That keeps
/// binary response chunks deterministic and removes the need to multiplex a
/// request id into every binary frame.
/// </summary>
public sealed class NovaLinkHostedService : BackgroundService
{
    private const int ChunkSize =
        512 * 1024;

    private const int MaxResponseBytes =
        64 * 1024 * 1024;

    private const int MaxControlMessageBytes =
        256 * 1024;

    private static readonly int MaxQueuedRequests =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_LINK_MAX_QUEUE"),
            out var maxQueuedRequests)
            ? Math.Clamp(
                maxQueuedRequests,
                1,
                32)
            : 4;

    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_LINK_REQUEST_TIMEOUT_SECONDS"),
                out var timeoutSeconds)
                ? Math.Clamp(
                    timeoutSeconds,
                    10,
                    120)
                : 45);

    private readonly NovaRequestDispatcher _dispatcher;
    private readonly ILogger<NovaLinkHostedService> _log;

    private readonly SemaphoreSlim _sendGate =
        new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase
        };

    public NovaLinkHostedService(
        NovaRequestDispatcher dispatcher,
        ILogger<NovaLinkHostedService> log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var rawUrl =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_LINK_URL");

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            _log.LogInformation(
                "NovaLink disabled: NOVASPARX_LINK_URL is not configured.");

            return;
        }

        if (!Uri.TryCreate(
                rawUrl,
                UriKind.Absolute,
                out var linkUri) ||
            linkUri.Scheme is not ("ws" or "wss"))
        {
            _log.LogError(
                "NovaLink disabled: NOVASPARX_LINK_URL must be an absolute ws:// or wss:// URL.");

            return;
        }

        if (
            linkUri.Scheme.Equals(
                "ws",
                StringComparison.OrdinalIgnoreCase) &&
            !linkUri.IsLoopback)
        {
            _log.LogError(
                "NovaLink disabled: remote connections must use wss:// so shared tokens are never sent over plaintext WebSocket.");

            return;
        }

        var tokens =
            ReadLinkTokens();

        if (tokens.Count == 0)
        {
            _log.LogError(
                "NovaLink disabled: NOVASPARX_SHARED_TOKEN or NOVASPARX_LINK_TOKEN is not configured.");

            return;
        }

        var reconnectAttempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket =
                    CreateSocket(
                        tokens[
                            reconnectAttempt %
                            tokens.Count]);

                _log.LogInformation(
                    "NovaLink connecting to {Host}.",
                    linkUri.Host);

                await socket.ConnectAsync(
                    linkUri,
                    stoppingToken);

                reconnectAttempt = 0;

                _log.LogInformation(
                    "NovaLink connected.");

                await RunConnectedAsync(
                    socket,
                    stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogWarning(
                        "NovaLink connection closed; reconnecting.");
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "NovaLink connection failed.");
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            reconnectAttempt++;

            var delay =
                ReconnectDelay(
                    reconnectAttempt);

            try
            {
                await Task.Delay(
                    delay,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static ClientWebSocket CreateSocket(
        string token)
    {
        var socket =
            new ClientWebSocket();

        socket.Options.KeepAliveInterval =
            TimeSpan.FromSeconds(20);

        socket.Options.SetRequestHeader(
            "Authorization",
            $"Bearer {token}");

        socket.Options.SetRequestHeader(
            "X-NovaSparx-Link-Token",
            token);

        socket.Options.SetRequestHeader(
            "X-NovaSparx-Version",
            LiveProviderService.BackendVersion);

        return socket;
    }

    private static List<string> ReadLinkTokens()
    {
        var output =
            new List<string>();

        foreach (var name in new[]
        {
            "NOVASPARX_SHARED_TOKEN",
            "NOVASPARX_LINK_TOKEN",
            "NOVASPARX_SHARED_TOKEN_PREVIOUS",
            "NOVASPARX_LINK_TOKEN_PREVIOUS"
        })
        {
            var token =
                Environment.GetEnvironmentVariable(
                    name)?
                    .Trim();

            if (
                string.IsNullOrWhiteSpace(
                    token) ||
                Encoding.UTF8
                    .GetByteCount(token) >
                    4096)
            {
                continue;
            }

            var duplicate = false;

            foreach (var existing in output)
            {
                if (string.Equals(
                        existing,
                        token,
                        StringComparison.Ordinal))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                output.Add(
                    token);
            }
        }

        return output;
    }

    private async Task RunConnectedAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var queuedRequests =
            new List<LinkRequest>();

        CancellationTokenSource?
            activeRequestCancellation =
                null;

        Task? activeRequestTask =
            null;

        string? activeRequestId =
            null;

        var receiveTask =
            ReceiveTextMessageAsync(
                socket,
                cancellationToken);

        void StartRequest(
            LinkRequest request)
        {
            activeRequestCancellation =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);

            activeRequestCancellation
                .CancelAfter(
                    RequestTimeout);

            activeRequestId =
                request.Id?
                    .Trim();

            activeRequestTask =
                HandleRequestAsync(
                    socket,
                    request,
                    activeRequestCancellation
                        .Token);
        }

        async Task ObserveActiveRequestAsync()
        {
            if (activeRequestTask is null)
                return;

            var task =
                activeRequestTask;

            var requestCancellation =
                activeRequestCancellation;

            try
            {
                await task;
            }
            catch (OperationCanceledException)
                when (
                    requestCancellation
                        ?.IsCancellationRequested ==
                    true)
            {
                var cancelledId =
                    activeRequestId;

                _log.LogDebug(
                    "NovaLink request {RequestId} cancelled.",
                    cancelledId);

                if (
                    !cancellationToken
                        .IsCancellationRequested &&
                    !string.IsNullOrWhiteSpace(
                        cancelledId) &&
                    socket.State ==
                        WebSocketState.Open)
                {
                    await SendControlAsync(
                        socket,
                        new
                        {
                            type =
                                "cancelled",
                            id =
                                cancelledId
                        },
                        cancellationToken);
                }
            }
            finally
            {
                requestCancellation
                    ?.Dispose();

                activeRequestCancellation =
                    null;

                activeRequestTask =
                    null;

                activeRequestId =
                    null;
            }
        }

        async Task ProcessControlAsync(
            string text)
        {
            LinkRequest? request;

            try
            {
                request =
                    JsonSerializer
                        .Deserialize<LinkRequest>(
                            text,
                            JsonOptions);
            }
            catch (JsonException ex)
            {
                _log.LogDebug(
                    ex,
                    "NovaLink received invalid JSON.");

                await SendControlAsync(
                    socket,
                    new
                    {
                        type =
                            "protocol_error",
                        error =
                            "Invalid JSON control message."
                    },
                    cancellationToken);

                return;
            }

            if (request is null)
                return;

            var type =
                request.Type?
                    .Trim()
                    .ToLowerInvariant();

            if (type == "hello")
                return;

            if (type == "ping")
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type = "pong",
                        time =
                            DateTimeOffset.UtcNow
                    },
                    cancellationToken);

                return;
            }

            if (type == "cancel")
            {
                var id =
                    request.Id?
                        .Trim();

                if (string.IsNullOrWhiteSpace(
                        id))
                {
                    return;
                }

                if (string.Equals(
                        activeRequestId,
                        id,
                        StringComparison.Ordinal))
                {
                    try
                    {
                        activeRequestCancellation
                            ?.Cancel();
                    }
                    catch
                    {
                        // It may have completed in the same instant.
                    }

                    return;
                }

                var removed =
                    queuedRequests.RemoveAll(
                        item =>
                            string.Equals(
                                item.Id,
                                id,
                                StringComparison.Ordinal));

                if (
                    removed > 0 &&
                    socket.State ==
                        WebSocketState.Open &&
                    !cancellationToken
                        .IsCancellationRequested)
                {
                    await SendControlAsync(
                        socket,
                        new
                        {
                            type =
                                "cancelled",
                            id
                        },
                        cancellationToken);
                }

                return;
            }

            if (type != "request")
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type =
                            "protocol_error",
                        error =
                            "Unsupported NovaLink control message."
                    },
                    cancellationToken);

                return;
            }

            if (string.IsNullOrWhiteSpace(
                    request.Id))
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type =
                            "protocol_error",
                        error =
                            "Request id is required."
                    },
                    cancellationToken);

                return;
            }

            var requestId =
                request.Id.Trim();

            if (
                requestId.Length > 128 ||
                !string.Equals(
                    requestId,
                    request.Id,
                    StringComparison.Ordinal) ||
                (request.Path?.Length ?? 0) >
                    4096 ||
                (request.Query?.Count ?? 0) >
                    32 ||
                (
                    request.Query is not null &&
                    request.Query.Any(
                        pair =>
                            (pair.Key?.Length ?? 0) > 128 ||
                            (pair.Value?.Length ?? 0) > 4096)
                ))
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type =
                            "cancelled",
                        id =
                            requestId,
                        reason =
                            "invalid-request"
                    },
                    cancellationToken);

                return;
            }

            if (activeRequestTask is null)
            {
                StartRequest(
                    request);

                return;
            }

            // Responses stay sequential so binary chunks remain unambiguous,
            // while the receive loop stays alive for cancel controls.
            if (
                queuedRequests.Count >=
                MaxQueuedRequests)
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type =
                            "cancelled",
                        id =
                            requestId,
                        reason =
                            "queue-full"
                    },
                    cancellationToken);

                return;
            }

            queuedRequests.Add(
                request);
        }

        try
        {
            while (
                socket.State ==
                    WebSocketState.Open &&
                !cancellationToken
                    .IsCancellationRequested)
            {
                // Process a control frame that is already waiting before
                // starting queued work. A queued cancellation therefore wins
                // before any heavy parser work starts.
                if (
                    activeRequestTask is null &&
                    receiveTask.IsCompleted)
                {
                    var text =
                        await receiveTask;

                    if (text is null)
                        break;

                    receiveTask =
                        ReceiveTextMessageAsync(
                            socket,
                            cancellationToken);

                    await ProcessControlAsync(
                        text);

                    continue;
                }

                if (
                    activeRequestTask is null &&
                    queuedRequests.Count > 0)
                {
                    var next =
                        queuedRequests[0];

                    queuedRequests.RemoveAt(
                        0);

                    StartRequest(
                        next);

                    continue;
                }

                if (activeRequestTask is null)
                {
                    var text =
                        await receiveTask;

                    if (text is null)
                        break;

                    receiveTask =
                        ReceiveTextMessageAsync(
                            socket,
                            cancellationToken);

                    await ProcessControlAsync(
                        text);

                    continue;
                }

                var completed =
                    await Task.WhenAny(
                        receiveTask,
                        activeRequestTask);

                if (
                    ReferenceEquals(
                        completed,
                        activeRequestTask))
                {
                    await ObserveActiveRequestAsync();
                    continue;
                }

                var incoming =
                    await receiveTask;

                if (incoming is null)
                    break;

                receiveTask =
                    ReceiveTextMessageAsync(
                        socket,
                        cancellationToken);

                await ProcessControlAsync(
                    incoming);
            }
        }
        finally
        {
            try
            {
                activeRequestCancellation
                    ?.Cancel();
            }
            catch {}

            if (activeRequestTask is not null)
            {
                await ObserveActiveRequestAsync();
            }

            queuedRequests.Clear();
        }

        if (socket.State is
            WebSocketState.Open or
            WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "NovaLink reconnect",
                    cancellationToken);
            }
            catch
            {
                // Socket is already unusable. The outer reconnect loop handles it.
            }
        }
    }

    private async Task HandleRequestAsync(
        ClientWebSocket socket,
        LinkRequest request,
        CancellationToken cancellationToken)
    {
        var query =
            MergeQuery(
                request.Path,
                request.Query);

        var route =
            StripQuery(
                request.Path);

        var response =
            await _dispatcher.DispatchAsync(
                request.Method ?? "GET",
                route,
                query,
                cancellationToken);

        if (response.Body.Length > MaxResponseBytes)
        {
            response =
                JsonError(
                    502,
                    "NovaSparx response exceeded the 64 MiB AutoLink limit.");
        }

        var chunks =
            response.Body.Length == 0
                ? 0
                : (response.Body.Length +
                   ChunkSize - 1) /
                  ChunkSize;

        await SendControlAsync(
            socket,
            new
            {
                type = "response",
                id = request.Id,
                status = response.Status,
                contentType =
                    response.ContentType,
                length =
                    response.Body.Length,
                chunks
            },
            cancellationToken);

        var offset = 0;

        while (offset < response.Body.Length)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var count =
                Math.Min(
                    ChunkSize,
                    response.Body.Length - offset);

            await SendBinaryAsync(
                socket,
                response.Body.AsMemory(
                    offset,
                    count),
                cancellationToken);

            offset += count;
        }

        await SendControlAsync(
            socket,
            new
            {
                type = "response_end",
                id = request.Id
            },
            cancellationToken);
    }

    private static async Task<string?>
        ReceiveTextMessageAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
    {
        using var stream =
            new MemoryStream();

        var buffer =
            new byte[16 * 1024];

        while (true)
        {
            var result =
                await socket.ReceiveAsync(
                    buffer,
                    cancellationToken);

            if (result.MessageType ==
                WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType !=
                WebSocketMessageType.Text)
            {
                throw new InvalidDataException(
                    "NovaLink accepts only text control messages from the worker.");
            }

            if (result.Count > 0)
            {
                stream.Write(
                    buffer,
                    0,
                    result.Count);
            }

            if (stream.Length >
                MaxControlMessageBytes)
            {
                throw new InvalidDataException(
                    "NovaLink control message exceeded 256 KiB.");
            }

            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(
            stream.GetBuffer(),
            0,
            checked((int)stream.Length));
    }

    private async Task SendControlAsync(
        ClientWebSocket socket,
        object value,
        CancellationToken cancellationToken)
    {
        var bytes =
            JsonSerializer
                .SerializeToUtf8Bytes(
                    value,
                    JsonOptions);

        await _sendGate.WaitAsync(
            cancellationToken);

        try
        {
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendBinaryAsync(
        ClientWebSocket socket,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(
            cancellationToken);

        try
        {
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static Dictionary<string, string>
        MergeQuery(
            string? rawPath,
            Dictionary<string, string>? supplied)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        if (supplied is not null)
        {
            foreach (var pair in supplied)
            {
                result[pair.Key] =
                    pair.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(rawPath))
            return result;

        var question =
            rawPath.IndexOf('?');

        if (question < 0 ||
            question + 1 >= rawPath.Length)
        {
            return result;
        }

        var query =
            rawPath[(question + 1)..];

        foreach (var piece in
                 query.Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var equals =
                piece.IndexOf('=');

            var key =
                equals < 0
                    ? piece
                    : piece[..equals];

            var value =
                equals < 0
                    ? string.Empty
                    : piece[(equals + 1)..];

            key =
                Uri.UnescapeDataString(
                    key.Replace('+', ' '));

            value =
                Uri.UnescapeDataString(
                    value.Replace('+', ' '));

            if (key.Length > 0 &&
                !result.ContainsKey(key))
            {
                result[key] =
                    value;
            }
        }

        return result;
    }

    private static string StripQuery(
        string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return "/";

        var value =
            rawPath.Trim();

        var question =
            value.IndexOf('?');

        if (question >= 0)
            value = value[..question];

        return value.Length == 0
            ? "/"
            : value;
    }

    private static TimeSpan ReconnectDelay(
        int attempt)
    {
        var exponent =
            Math.Clamp(
                attempt - 1,
                0,
                5);

        var seconds =
            Math.Min(
                30,
                1 << exponent);

        // Small jitter prevents a fleet of restarted containers reconnecting
        // at exactly the same instant.
        var jitterMilliseconds =
            Random.Shared.Next(
                0,
                750);

        return TimeSpan.FromMilliseconds(
            seconds * 1000 +
            jitterMilliseconds);
    }

    private static DispatchResponse JsonError(
        int status,
        string error)
    {
        var body =
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    state = "error",
                    error
                },
                JsonOptions);

        return new DispatchResponse(
            Status:
                status,

            ContentType:
                "application/json; charset=utf-8",

            Body:
                body);
    }

    private sealed class LinkRequest
    {
        public string? Type { get; init; }
        public string? Id { get; init; }
        public string? Method { get; init; }
        public string? Path { get; init; }
        public Dictionary<string, string>? Query { get; init; }
    }
}
