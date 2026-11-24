using System.Text;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using Security_Camera_Backend.Configuration;

namespace Security_Camera_Backend.Services.Mqtt;

/// <summary>
/// Singleton MQTT client with automatic reconnect.
///
/// Publishing never blocks the HTTP request path: <see cref="PublishJsonAsync"/> awaits a broker
/// round-trip with a short timeout and reports failure, so callers (the notifier, the MediaMTX
/// webhook) can log and move on. A dropped connection is retried in the background with
/// exponential backoff, and the client reconnects lazily on the next publish.
/// </summary>
public sealed class MqttPublisherService : IMqttPublisherService, IAsyncDisposable
{
    private const int MaxBackoffSeconds = 30;

    private readonly MqttOptions _options;
    private readonly ILogger<MqttPublisherService> _logger;
    private readonly MqttClientFactory _factory = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private IMqttClient? _client;
    private bool _connected;
    private bool _disposed;
    private int _backoffSeconds = 1;

    public MqttPublisherService(
        IOptions<MqttOptions> options,
        ILogger<MqttPublisherService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsConfigured;

    public async Task<bool> PublishJsonAsync(
        string topic,
        string payload,
        bool retain,
        CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogDebug("MQTT is disabled; dropping retained={Retain} publish to {Topic}.", retain, topic);
            return false;
        }

        if (!await EnsureConnectedAsync(ct))
        {
            return false;
        }

        var message = _factory.CreateApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .WithContentType("application/json")
            .Build();

        try
        {
            var result = await _client!.PublishAsync(message, ct);

            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "MQTT publish to {Topic} failed with reason {ReasonCode} ({ReasonString}).",
                    topic, result.ReasonCode, result.ReasonString);
                return false;
            }

            _logger.LogDebug(
                "Published {Bytes} byte(s) to {Topic} (retain={Retain}).",
                Encoding.UTF8.GetByteCount(payload), topic, retain);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MQTT publish to {Topic} threw; marking connection as down.", topic);
            _connected = false;
            return false;
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected && _client is { IsConnected: true })
        {
            return true;
        }

        await _connectLock.WaitAsync(ct);

        try
        {
            if (_connected && _client is { IsConnected: true })
            {
                return true;
            }

            _client ??= _factory.CreateMqttClient();
            _client.DisconnectedAsync += OnDisconnectedAsync;

            var builder = _factory.CreateClientOptionsBuilder()
                .WithTcpServer(_options.Host, _options.Port)
                .WithClientId($"{_options.ClientId}-{Environment.MachineName}")
                .WithCleanSession(false)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithTimeout(TimeSpan.FromSeconds(Math.Max(1, _options.PublishTimeoutSeconds)));

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                builder = builder.WithCredentials(_options.Username, _options.Password);
            }

            var connectResult = await _client.ConnectAsync(builder.Build(), ct);

            _connected = true;
            _backoffSeconds = 1;
            _logger.LogInformation(
                "Connected to MQTT broker {Host}:{Port} as {Username} (session present: {SessionPresent}).",
                _options.Host, _options.Port, _options.Username, connectResult.IsSessionPresent);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _connected = false;
            _logger.LogWarning(
                ex,
                "MQTT connect to {Host}:{Port} failed; retrying in {Backoff}s.",
                _options.Host, _options.Port, _backoffSeconds);

            ScheduleReconnect();
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _connected = false;

        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning(
            args.Exception,
            "MQTT connection lost ({Reason}); scheduling reconnect.",
            args.Reason);

        ScheduleReconnect();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fire-and-forget reconnect. Publishing also retries on demand, so this timer only matters
    /// when the backend is quiet and would otherwise stay disconnected.
    /// </summary>
    private void ScheduleReconnect()
    {
        if (_disposed)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(_backoffSeconds);
        _backoffSeconds = Math.Min(MaxBackoffSeconds, _backoffSeconds * 2);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _disposeCts.Token);
                await EnsureConnectedAsync(_disposeCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background MQTT reconnect attempt failed.");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _disposeCts.CancelAsync();

        if (_client is not null)
        {
            _client.DisconnectedAsync -= OnDisconnectedAsync;

            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MQTT disconnect during shutdown failed.");
            }
            finally
            {
                _client.Dispose();
                _client = null;
            }
        }

        _connectLock.Dispose();
        _disposeCts.Dispose();
    }
}
