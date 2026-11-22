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
    /// After every successful connect (clean session) the attempt also subscribes to
    /// <see cref="DeviceSettings.StatusTopic"/> (QoS 0); messages on that topic are raised through
    /// <see cref="StatusMessageReceived"/>. A failed subscription is logged and never affects publishing.
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
        private int _connectionGeneration;
        private DateTimeOffset? _outageStartedAt;
        private bool _disposed;
        private string? _subscribedTopic;   // status topic the current client subscribed to (or tried)
        private Task? _resubscribe;

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

        /// <summary>
        /// Raised (from an MQTT client thread) with the raw payload of each message received on
        /// the current <see cref="DeviceSettings.StatusTopic"/>. Handler exceptions are caught.
        /// </summary>
        public event Action<byte[]>? StatusMessageReceived;

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

        /// <summary>
        /// Number of connections established so far. It changes with every successful
        /// (re)connect, so a caller can tell a connection made after some point in time (e.g. after
        /// provisioning a device) from an older one that is still up.
        /// </summary>
        public int ConnectionGeneration
        {
            get
            {
                lock (_gate)
                {
                    return _connectionGeneration;
                }
            }
        }

        /// <summary>Test hook: the in-flight (or last) background status resubscription.</summary>
        internal Task PendingResubscribe
        {
            get
            {
                lock (_gate)
                {
                    return _resubscribe ?? Task.CompletedTask;
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

                var initial = TimeSpan.FromMilliseconds(settings.ReconnectInitialBackoffMs);
                var max = TimeSpan.FromMilliseconds(settings.ReconnectMaxBackoffMs);
                if (_backoff == null)
                {
                    _backoff = new ReconnectBackoff(initial, max, _clock);
                }
                else if (_backoff.InitialDelay != initial || _backoff.MaxDelay != max)
                {
                    // Keep the connection/stability state; only the delays change.
                    _backoff.Reconfigure(initial, max);
                }

                if (previous != null && previous.EndpointDiffers(settings))
                {
                    _reconnectRequested = true;
                    _backoff.Reset();
                    _logger.LogInformation("MQTT broker endpoint changed to {Endpoint}; reconnecting.", DescribeEndpoint(settings));
                }
                else
                {
                    EnsureStatusSubscriptionLocked();
                }
            }
        }

        /// <summary>
        /// Resubscribes in the background when the connected client's status subscription does not
        /// match the current <see cref="DeviceSettings.StatusTopic"/>: after a topic change, also one
        /// made while a connect attempt (which subscribes to its own snapshot) was in flight. Called
        /// from <see cref="Configure"/> and every <see cref="MaintainConnection"/> tick.
        /// </summary>
        private void EnsureStatusSubscriptionLocked()
        {
            if (_settings == null || !IsConnectedLocked || _subscribedTopic == null
                || _subscribedTopic == _settings.StatusTopic || _resubscribe is { IsCompleted: false })
            {
                return;
            }

            var client = _client!;
            var previousTopic = _subscribedTopic;
            var settings = _settings;
            _subscribedTopic = settings.StatusTopic;
            _resubscribe = Task.Run(() => ResubscribeAsync(client, previousTopic, settings));
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
                    EnsureStatusSubscriptionLocked();
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
            Task? resubscribe;
            IMqttClient? client;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                attempt = _attempt;
                resubscribe = _resubscribe;
                client = _client;
                _client = null;
            }

            _shutdown.Cancel();

            // Background work that may still use _shutdown: the connect attempt and a resubscribe.
            var running = new[] { attempt, resubscribe }.OfType<Task>().Where(task => !task.IsCompleted).ToArray();
            if (running.Length > 0)
            {
                try
                {
                    await Task.WhenAll(running).WaitAsync(ShutdownAttemptWait).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down; an unfinished attempt disposes its own client.
                }
            }

            // Dispose the token source only once nothing can still be using it.
            var unfinished = running.Where(task => !task.IsCompleted).ToArray();
            if (unfinished.Length == 0)
            {
                _shutdown.Dispose();
            }
            else
            {
                _ = Task.WhenAll(unfinished).ContinueWith(
                    task =>
                    {
                        _ = task.Exception;   // observe, never surface as an unobserved task exception
                        _shutdown.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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
                    client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
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

                    // Clean session: the broker forgets subscriptions, so subscribe after every connect.
                    try
                    {
                        await SubscribeStatusAsync(client, settings, shutdown).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Shutdown cancelled the subscription: never leak the connected client.
                        await CloseClientAsync(client, ShutdownDisconnectTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                        throw;
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

        private async Task SubscribeStatusAsync(IMqttClient client, DeviceSettings settings, CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.ConnectTimeoutMs);
                var options = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(filter => filter.WithTopic(settings.StatusTopic).WithAtMostOnceQoS())
                    .Build();
                var result = await client.SubscribeAsync(options, timeout.Token).ConfigureAwait(false);
                var code = result.Items.FirstOrDefault()?.ResultCode;
                if (code is MqttClientSubscribeResultCode.GrantedQoS0 or MqttClientSubscribeResultCode.GrantedQoS1 or MqttClientSubscribeResultCode.GrantedQoS2)
                {
                    _logger.LogDebug("Subscribed to device status topic {Topic}.", settings.StatusTopic);
                }
                else
                {
                    _logger.LogWarning("MQTT broker refused the subscription to {Topic} ({Code}); device status is unavailable.", settings.StatusTopic, code);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Status is optional: publishing continues without it.
                _logger.LogWarning("MQTT subscribe to {Topic} failed: {Error}. Device status is unavailable until the next reconnect.", settings.StatusTopic, Describe(ex, settings.ConnectTimeoutMs));
            }
        }

        /// <summary>
        /// Best-effort background resubscription; never faults. It may start just before
        /// <see cref="DisposeAsync"/>, so even reading the shutdown token is guarded.
        /// </summary>
        private async Task ResubscribeAsync(IMqttClient client, string previousTopic, DeviceSettings settings)
        {
            try
            {
                var shutdown = _shutdown.Token;   // throws ObjectDisposedException once disposed
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                    timeout.CancelAfter(settings.ConnectTimeoutMs);
                    await client.UnsubscribeAsync(new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(previousTopic).Build(), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!shutdown.IsCancellationRequested)
                {
                    // Messages on the old topic are ignored anyway (see OnApplicationMessageReceivedAsync).
                }

                shutdown.ThrowIfCancellationRequested();
                await SubscribeStatusAsync(client, settings, shutdown).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Disposed (ObjectDisposedException) or shutting down (cancellation): nothing to
                // resubscribe. Other failures are logged by SubscribeStatusAsync itself.
            }
        }

        private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            string? statusTopic;
            lock (_gate)
            {
                statusTopic = _disposed ? null : _settings?.StatusTopic;
            }

            var handler = StatusMessageReceived;
            if (handler == null || statusTopic == null || args.ApplicationMessage.Topic != statusTopic)
            {
                return Task.CompletedTask;
            }

            try
            {
                handler(args.ApplicationMessage.PayloadSegment.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Status message handler failed: {Error}", ex.Message);
            }

            return Task.CompletedTask;
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
                _subscribedTopic = settings.StatusTopic;
                _wasConnected = true;
                _connectionGeneration++;
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
