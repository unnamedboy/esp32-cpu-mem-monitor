using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Background batch sender: drains <see cref="LokiEntryQueue"/> and pushes batches to Loki.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A batch is sent when <see cref="LokiSettings.BatchMaxEntries"/> entries are queued or
    /// <see cref="LokiSettings.FlushIntervalMs"/> has passed since the previous send.</item>
    /// <item>429, 5xx, timeouts and network errors keep the batch and retry it with exponential
    /// backoff (1 s doubling to 60 s, <see cref="ReconnectBackoff"/>), honouring Retry-After up to
    /// 60 s; new entries keep queueing (bounded, drop-oldest) meanwhile.</item>
    /// <item>Other 4xx (and 3xx) drop the batch; logged once per status until a push succeeds.</item>
    /// <item>Queue drops are reported to the local log at most once per minute.</item>
    /// <item>Every message of the sink goes to the local log only: its logger category is
    /// excluded by <see cref="LokiLoggerProvider"/>, and no message contains the token or URL path.</item>
    /// <item><see cref="StopAsync"/> flushes what is queued within a time budget.</item>
    /// </list>
    /// Failures never propagate to callers; sources only ever touch the queue.
    /// </remarks>
    public sealed class LokiSink : IAsyncDisposable
    {
        public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan DropReportInterval = TimeSpan.FromMinutes(1);
        private const int FailureSummaryInterval = 20;

        private readonly ILogger _logger;
        private readonly LokiPushClient _client;
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly HashSet<int> _loggedDropStatuses = new();
        private readonly ReconnectBackoff _backoff;

        private LokiSettings _settings = new();
        private List<LokiEntry>? _pending;
        private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
        private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
        private DateTimeOffset _lastDropReport;
        private int _consecutiveFailures;
        private long _droppedByServer;
        private Task? _loop;
        private bool _disposed;

        public LokiSink(LokiEntryQueue queue, ILogger<LokiSink> logger, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
        {
            Queue = queue;
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _client = new LokiPushClient(handler);
            _backoff = new ReconnectBackoff(InitialRetryDelay, MaxRetryDelay, _clock);
            _lastDropReport = _clock();
            queue.ThresholdReached = Wake;
        }

        public LokiEntryQueue Queue { get; }

        /// <summary>Settings currently in effect.</summary>
        public LokiSettings Settings => Volatile.Read(ref _settings);

        /// <summary>Entries in the batch waiting for a retry.</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _pending?.Count ?? 0;
                }
            }
        }

        /// <summary>Earliest time of the next retry (MinValue when not backing off).</summary>
        public DateTimeOffset RetryAt
        {
            get
            {
                lock (_gate)
                {
                    return _retryAt;
                }
            }
        }

        /// <summary>Entries in batches the server rejected (4xx) and that were dropped.</summary>
        public long DroppedByServer => Interlocked.Read(ref _droppedByServer);

        /// <summary>
        /// Applies settings. Disabling stops collection and discards queued and pending entries;
        /// a changed endpoint or credentials retries immediately.
        /// </summary>
        public void Configure(LokiSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            LokiSettings previous;
            lock (_gate)
            {
                previous = _settings;
                _settings = settings;
                if (!settings.IsActive)
                {
                    _pending = null;
                }

                if (previous.Url != settings.Url || previous.AuthMode != settings.AuthMode || previous.Username != settings.Username
                    || previous.Token != settings.Token || previous.TenantId != settings.TenantId || previous.IsActive != settings.IsActive)
                {
                    _backoff.Reset();
                    _retryAt = DateTimeOffset.MinValue;
                    _consecutiveFailures = 0;
                    _loggedDropStatuses.Clear();
                }
            }

            Queue.MinLogLevel = settings.MinLevel;
            Queue.BatchThreshold = settings.BatchMaxEntries;
            Queue.SetCapacity(settings.QueueCapacity);
            Queue.Accepting = settings.IsActive && !_disposed;
            if (!settings.IsActive)
            {
                Queue.Clear();
            }

            Wake();
        }

        /// <summary>Starts the background sender (once).</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_loop != null || _disposed)
                {
                    return;
                }

                _loop = Task.Run(() => RunAsync(_stop.Token));
            }
        }

        /// <summary>
        /// Stops the sender and flushes queued entries within <paramref name="budget"/> (and
        /// <paramref name="cancellationToken"/>). Never throws; never waits longer than the budget.
        /// </summary>
        public async Task StopAsync(TimeSpan budget, CancellationToken cancellationToken = default)
        {
            var started = _clock();
            using var budgetSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetSource.CancelAfter(budget);
            var token = budgetSource.Token;

            Task? loop;
            lock (_gate)
            {
                loop = _loop;
            }

            _stop.Cancel();
            if (loop != null)
            {
                try
                {
                    await loop.WaitAsync(token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Budget exhausted or the loop faulted; it logs its own problems.
                }
            }

            var flushed = 0;
            var settings = Settings;
            try
            {
                while (settings.IsActive && !token.IsCancellationRequested && (PendingCount > 0 || Queue.Count > 0))
                {
                    var result = await FlushOnceAsync(token).ConfigureAwait(false);
                    if (result?.Outcome != LokiPushOutcome.Success)
                    {
                        break;
                    }

                    flushed++;
                }
            }
            catch (OperationCanceledException)
            {
                // Budget exhausted.
            }

            Queue.Accepting = false;
            var left = PendingCount + Queue.Count;
            if (left > 0 && settings.IsActive)
            {
                _logger.LogWarning("Loki: {Count} entries not sent at shutdown (flush budget {Budget:F1} s).", left, budget.TotalSeconds);
            }
            else if (flushed > 0)
            {
                _logger.LogDebug("Loki: flushed {Batches} batch(es) at shutdown in {Ms:F0} ms.", flushed, (_clock() - started).TotalMilliseconds);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Queue.Accepting = false;
            _stop.Cancel();
            Task? loop;
            lock (_gate)
            {
                loop = _loop;
            }

            if (loop != null)
            {
                try
                {
                    await loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            _client.Dispose();
        }

        /// <summary>
        /// True when a send is due: a batch is waiting for its retry time, a full batch is queued,
        /// or entries are queued and the flush interval has passed since the last send.
        /// </summary>
        public bool IsFlushDue(DateTimeOffset now)
        {
            var settings = Settings;
            if (!settings.IsActive)
            {
                return false;
            }

            lock (_gate)
            {
                if (now < _retryAt)
                {
                    return false;
                }

                if (_pending != null)
                {
                    return true;
                }

                var count = Queue.Count;
                return count >= settings.BatchMaxEntries
                    || (count > 0 && now - _lastFlush >= TimeSpan.FromMilliseconds(settings.FlushIntervalMs));
            }
        }

        /// <summary>
        /// Sends the pending (retry) batch, or the next batch of up to
        /// <see cref="LokiSettings.BatchMaxEntries"/> entries. Returns null when there was nothing
        /// to send. Cancellation keeps the batch as pending and rethrows.
        /// </summary>
        public async Task<LokiPushResult?> FlushOnceAsync(CancellationToken cancellationToken)
        {
            var settings = Settings;
            List<LokiEntry> batch;
            lock (_gate)
            {
                batch = _pending ?? Queue.DequeueBatch(settings.BatchMaxEntries);
                _pending = null;
                _lastFlush = _clock();
            }

            if (batch.Count == 0 || !settings.IsActive)
            {
                return null;
            }

            LokiPushResult result;
            try
            {
                result = await _client.PushAsync(settings, batch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _pending ??= batch;
                }

                throw;
            }

            switch (result.Outcome)
            {
                case LokiPushOutcome.Success:
                    OnSuccess();
                    break;
                case LokiPushOutcome.Retry:
                    OnRetry(batch, result);
                    break;
                default:
                    OnDrop(batch, result);
                    break;
            }

            return result;
        }

        private void OnSuccess()
        {
            int failures;
            lock (_gate)
            {
                failures = _consecutiveFailures;
                _consecutiveFailures = 0;
                _backoff.Reset();
                _retryAt = DateTimeOffset.MinValue;
                _loggedDropStatuses.Clear();
            }

            if (failures > 0)
            {
                _logger.LogInformation("Loki push recovered after {Failures} failed attempt(s).", failures);
            }
        }

        private void OnRetry(List<LokiEntry> batch, LokiPushResult result)
        {
            TimeSpan delay;
            int failures;
            lock (_gate)
            {
                // Only keep the batch if the sink is still enabled.
                if (Settings.IsActive)
                {
                    _pending = batch;
                }

                delay = _backoff.RecordFailure();
                if (result.RetryAfter is { } retryAfter && retryAfter > delay)
                {
                    delay = retryAfter < MaxRetryDelay ? retryAfter : MaxRetryDelay;
                }

                _retryAt = _clock() + delay;
                failures = ++_consecutiveFailures;
            }

            if (failures == 1)
            {
                _logger.LogWarning("Loki push failed ({Result}); keeping {Count} entries, retrying in {Delay:F0} s.", result, batch.Count, delay.TotalSeconds);
            }
            else if (failures % FailureSummaryInterval == 0)
            {
                _logger.LogInformation("Loki push still failing after {Failures} attempts ({Result}); next retry in {Delay:F0} s.", failures, result, delay.TotalSeconds);
            }
            else
            {
                _logger.LogDebug("Loki push attempt {Failures} failed ({Result}); next retry in {Delay:F0} s.", failures, result, delay.TotalSeconds);
            }
        }

        private void OnDrop(List<LokiEntry> batch, LokiPushResult result)
        {
            Interlocked.Add(ref _droppedByServer, batch.Count);
            bool first;
            lock (_gate)
            {
                first = _loggedDropStatuses.Add(result.StatusCode ?? 0);
            }

            if (first)
            {
                _logger.LogWarning(
                    "Loki rejected a batch ({Result}); {Count} entries dropped. Check Url, credentials and tenant. Further rejections like this are logged at Debug until a push succeeds.",
                    result, batch.Count);
            }
            else
            {
                _logger.LogDebug("Loki rejected a batch ({Result}); {Count} entries dropped.", result, batch.Count);
            }
        }

        private async Task RunAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    ReportDropsIfDue();
                    var now = _clock();
                    if (IsFlushDue(now))
                    {
                        await FlushOnceAsync(stop).ConfigureAwait(false);
                        continue;
                    }

                    await WaitAsync(NextWait(now), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let the sender die; wait a little to avoid a hot loop.
                    _logger.LogError(ex, "Unexpected error in the Loki sender.");
                    try
                    {
                        await Task.Delay(InitialRetryDelay, stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private TimeSpan NextWait(DateTimeOffset now)
        {
            var settings = Settings;
            if (!settings.IsActive)
            {
                return DropReportInterval;
            }

            var interval = TimeSpan.FromMilliseconds(settings.FlushIntervalMs);
            TimeSpan wait;
            lock (_gate)
            {
                wait = now < _retryAt ? _retryAt - now
                    : Queue.Count > 0 ? _lastFlush + interval - now
                    : interval;
            }

            return wait < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10)
                : wait > DropReportInterval ? DropReportInterval
                : wait;
        }

        private async Task WaitAsync(TimeSpan wait, CancellationToken stop)
        {
            await _wake.WaitAsync(wait, stop).ConfigureAwait(false);
        }

        private void Wake()
        {
            try
            {
                if (_wake.CurrentCount == 0)
                {
                    _wake.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ReportDropsIfDue()
        {
            var now = _clock();
            if (now - _lastDropReport < DropReportInterval)
            {
                return;
            }

            var dropped = Queue.TakeDroppedCount();
            if (dropped > 0)
            {
                _logger.LogWarning(
                    "Loki queue full: dropped the {Count} oldest entries in the last {Seconds:F0} s (capacity {Capacity}). Loki may be unreachable or too slow.",
                    dropped, (now - _lastDropReport).TotalSeconds, Queue.Capacity);
            }

            _lastDropReport = now;
        }
    }
}
