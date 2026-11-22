using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Owns the MQTT client, its connection state and the reconnect schedule.
    /// </summary>
    /// <remarks>
    /// The sampling loop calls <see cref="MaintainConnection"/> once per tick. It never waits for
    /// network I/O: when disconnected and the backoff deadline has passed, it starts one
    /// background connect attempt (Host first, then FallbackAddress if set, each bounded by
    /// <see cref="DeviceSettings.ConnectTimeoutMs"/>). While an attempt is in flight or the
    /// broker is unreachable, <see cref="PublishAsync"/> returns false without blocking.
    /// A tray notification is raised once per outage, plus one "reconnected" notice.
    /// <see cref="DisposeAsync"/> cancels any in-flight connect immediately.
    /// </remarks>
    public sealed class MqttConnection : IAsyncDisposable
    {
        private const int FailureSummaryInterval = 20;
        private const int ShutdownDisconnectTimeoutMs = 500;
        private static readonly TimeSpan ShutdownAttemptWait = TimeSpan.FromSeconds(2);

        private readonly ILogger<MqttConnection> _logger;
        private readonly MqttFactory _factory = new();
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _shutdown = new();

        private DeviceSettings? _settings;
        private ReconnectBackoff? _backoff;
        private IMqttClient? _client;
        private string? _connectedAddress;
        private Task? _attempt;
        private bool _reconnectRequested;   // endpoint changed: reconnect now, bypassing backoff
        private bool _dropRequested;        // publish failed: treat the current client as lost
        private bool _wasConnected;
        private bool _outageNotified;
        private int _outageFailures;
        private DateTimeOffset? _outageStartedAt;
        private bool _disposed;

        public MqttConnection(ILogger<MqttConnection> logger, Func<DateTimeOffset>? clock = null)
        {
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Raised (from a background thread) for user-visible connection events. The outage is
        /// only marked as notified when a handler was attached and ran.
        /// </summary>
        public Action<string, string, ToolTipIcon>? OnNotification { get; set; }

        /// <summary>True when a connected client is ready for publishing.</summary>
        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return IsConnectedLocked;
                }
            }
        }

        /// <summary>Test hook: the in-flight (or last) connect attempt.</summary>
        internal Task PendingAttempt
        {
            get
            {
                lock (_gate)
                {
                    return _attempt ?? Task.CompletedTask;
                }
            }
        }

        /// <summary>Test hook: failed connect attempts in the current outage.</summary>
        internal int FailedAttemptsInOutage
        {
            get
            {
                lock (_gate)
                {
                    return _outageFailures;
                }
            }
        }

        private bool IsConnectedLocked =>
            !_disposed
            && !_reconnectRequested
            && !_dropRequested
            && (_attempt == null || _attempt.IsCompleted)
            && _client?.IsConnected == true;

        /// <summary>
        /// Applies new settings. A changed host, fallback address or port drops the current
        /// connection and reconnects on the next <see cref="MaintainConnection"/> call.
        /// </summary>
        public void Configure(DeviceSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            lock (_gate)
            {
                var previous = _settings;
                _settings = settings;

                if (_backoff == null
                    || previous == null
                    || previous.ReconnectInitialBackoffMs != settings.ReconnectInitialBackoffMs
                    || previous.ReconnectMaxBackoffMs != settings.ReconnectMaxBackoffMs)
                {
                    _backoff = new ReconnectBackoff(
                        TimeSpan.FromMilliseconds(settings.ReconnectInitialBackoffMs),
                        TimeSpan.FromMilliseconds(settings.ReconnectMaxBackoffMs),
                        _clock);
                }

                if (previous != null && previous.EndpointDiffers(settings))
                {
                    _reconnectRequested = true;
                    _backoff.Reset();
                    _logger.LogInformation("MQTT broker endpoint changed to {Endpoint}; reconnecting.", DescribeEndpoint(settings));
                }
            }
        }

        /// <summary>
        /// Called once per sampling tick. Starts at most one background connect attempt and
        /// returns immediately.
        /// </summary>
        public void MaintainConnection()
        {
            lock (_gate)
            {
                if (_disposed || _settings == null || _backoff == null)
                {
                    return;
                }

                if (_attempt is { IsCompleted: false })
                {
                    return;
                }

                var connected = _client?.IsConnected == true && !_dropRequested;
                if (connected && !_reconnectRequested)
                {
                    return;
                }

                if (!connected && _wasConnected)
                {
                    BeginOutageLocked($"connection to {_connectedAddress}:{_settings.Port} lost");
                }

                if (!_reconnectRequested && !_backoff.IsAttemptDue)
                {
                    return;
                }

                _reconnectRequested = false;
                _dropRequested = false;
                _wasConnected = false;
                var oldClient = _client;
                _client = null;
                _connectedAddress = null;
                var snapshot = _settings;
                _attempt = Task.Run(() => RunAttemptAsync(snapshot, oldClient));
            }
        }

        /// <summary>
        /// Publishes one QoS 0, non-retained message. Returns false (without waiting) when not
        /// connected; a failed publish marks the connection lost (reconnect follows the backoff).
        /// </summary>
        public async Task<bool> PublishAsync(string topic, string payload, CancellationToken cancellationToken)
        {
            IMqttClient client;
            int timeoutMs;
            lock (_gate)
            {
                if (!IsConnectedLocked)
                {
                    return false;
                }

                client = _client!;
                timeoutMs = _settings!.ConnectTimeoutMs;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
                timeout.CancelAfter(timeoutMs);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();
                await client.PublishAsync(message, timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (!_disposed && ReferenceEquals(_client, client) && !_dropRequested && !_reconnectRequested)
                    {
                        _dropRequested = true;
                        BeginOutageLocked($"publish to {topic} failed ({Describe(ex, timeoutMs)})");
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Cancels any in-flight connect attempt first, then closes the client. Completes within
        /// about a second even when the broker is unreachable.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Task? attempt;
            IMqttClient? client;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                attempt = _attempt;
                client = _client;
                _client = null;
            }

            _shutdown.Cancel();

            if (attempt != null)
            {
                try
                {
                    await attempt.WaitAsync(ShutdownAttemptWait).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down; an unfinished attempt disposes its own client.
                }
            }

            if (client != null)
            {
                await CloseClientAsync(client, ShutdownDisconnectTimeoutMs, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task RunAttemptAsync(DeviceSettings settings, IMqttClient? oldClient)
        {
            var shutdown = _shutdown.Token;
            try
            {
                if (oldClient != null)
                {
                    await CloseClientAsync(oldClient, settings.ConnectTimeoutMs, shutdown).ConfigureAwait(false);
                }

                var failures = new List<string>();
                foreach (var address in CandidateAddresses(settings))
                {
                    if (shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    var client = _factory.CreateMqttClient();
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                        timeout.CancelAfter(settings.ConnectTimeoutMs);
                        var options = new MqttClientOptionsBuilder()
                            .WithTcpServer(address, settings.Port)
                            .WithCleanSession()
                            .WithTimeout(TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs))
                            .Build();
                        await client.ConnectAsync(options, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        client.Dispose();
                        if (shutdown.IsCancellationRequested)
                        {
                            return;
                        }

                        failures.Add($"{address}:{settings.Port} ({Describe(ex, settings.ConnectTimeoutMs)})");
                        continue;
                    }

                    if (!OnConnected(client, address, settings))
                    {
                        await CloseClientAsync(client, ShutdownDisconnectTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                    }

                    return;
                }

                OnAttemptFailed(settings, string.Join("; ", failures));
            }
            catch (Exception ex) when (!shutdown.IsCancellationRequested)
            {
                _logger.LogError(ex, "Unexpected error during MQTT connect attempt.");
                OnAttemptFailed(settings, ex.Message);
            }
            catch (Exception)
            {
                // Cancelled by shutdown.
            }
        }

        private bool OnConnected(IMqttClient client, string address, DeviceSettings settings)
        {
            string? reconnectedMessage = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _client = client;
                _connectedAddress = address;
                _wasConnected = true;
                _backoff!.RecordConnected();

                var via = string.Equals(address, settings.Host, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : $" (fallback for {settings.Host})";
                if (_outageStartedAt is { } started)
                {
                    _logger.LogInformation(
                        "Reconnected to MQTT broker {Address}:{Port}{Via} after {Failures} failed attempt(s); outage lasted {Seconds:F0} s.",
                        address, settings.Port, via, _outageFailures, (_clock() - started).TotalSeconds);
                }
                else
                {
                    _logger.LogInformation("Connected to MQTT broker {Address}:{Port}{Via}.", address, settings.Port, via);
                }

                if (_outageNotified)
                {
                    reconnectedMessage = $"Connected to ESP32 MQTT broker {address}:{settings.Port}{via}.";
                    _outageNotified = false;
                }

                _outageStartedAt = null;
                _outageFailures = 0;
            }

            if (reconnectedMessage != null)
            {
                Notify("MQTT reconnected", reconnectedMessage, ToolTipIcon.Info);
            }

            return true;
        }

        private void OnAttemptFailed(DeviceSettings settings, string details)
        {
            bool notify;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _outageStartedAt ??= _clock();
                _outageFailures++;
                var delay = _backoff!.RecordFailure();
                if (_outageFailures == 1)
                {
                    _logger.LogWarning("MQTT connect failed: {Details}. Retrying in {Delay:F1} s.", details, delay.TotalSeconds);
                }
                else if (_outageFailures % FailureSummaryInterval == 0)
                {
                    _logger.LogInformation("MQTT still disconnected after {Failures} attempts: {Details}. Next retry in {Delay:F1} s.", _outageFailures, details, delay.TotalSeconds);
                }
                else
                {
                    _logger.LogDebug("MQTT connect attempt {Failures} failed: {Details}. Next retry in {Delay:F1} s.", _outageFailures, details, delay.TotalSeconds);
                }

                notify = !_outageNotified;
            }

            if (notify && Notify(
                    "MQTT connection failed",
                    $"Unable to connect to ESP32 MQTT broker ({DescribeEndpoint(settings)}).\n" +
                    "Check that the ESP32 is powered on and on the same Wi-Fi. Retrying in the background.",
                    ToolTipIcon.Error))
            {
                lock (_gate)
                {
                    _outageNotified = true;
                }
            }
        }

        private void BeginOutageLocked(string reason)
        {
            _wasConnected = false;
            _outageStartedAt ??= _clock();
            var delay = _backoff?.RecordDisconnected();
            if (delay is { } wait)
            {
                _logger.LogWarning("MQTT {Reason}; connection was short-lived, next attempt in {Delay:F1} s.", reason, wait.TotalSeconds);
            }
            else
            {
                _logger.LogWarning("MQTT {Reason}.", reason);
            }
        }

        /// <summary>Returns true when a handler was attached and ran without throwing.</summary>
        private bool Notify(string title, string message, ToolTipIcon icon)
        {
            var handler = OnNotification;
            if (handler == null)
            {
                return false;
            }

            try
            {
                handler(title, message, icon);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Notification handler failed: {Error}", ex.Message);
                return false;
            }
        }

        private static IEnumerable<string> CandidateAddresses(DeviceSettings settings)
        {
            yield return settings.Host;
            if (!string.IsNullOrWhiteSpace(settings.FallbackAddress)
                && !string.Equals(settings.FallbackAddress, settings.Host, StringComparison.OrdinalIgnoreCase))
            {
                yield return settings.FallbackAddress;
            }
        }

        private static string DescribeEndpoint(DeviceSettings settings) =>
            string.IsNullOrWhiteSpace(settings.FallbackAddress)
                ? $"{settings.Host}:{settings.Port}"
                : $"{settings.Host}:{settings.Port}, fallback {settings.FallbackAddress}";

        private static string Describe(Exception ex, int timeoutMs) =>
            ex is OperationCanceledException or TimeoutException or MQTTnet.Exceptions.MqttCommunicationTimedOutException
                ? $"timed out after {timeoutMs} ms"
                : ex.GetBaseException().Message;

        private static async Task CloseClientAsync(IMqttClient client, int timeoutMs, CancellationToken cancellationToken)
        {
            try
            {
                if (client.IsConnected)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(timeoutMs);
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best effort: the broker may already be gone, or we are shutting down.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
