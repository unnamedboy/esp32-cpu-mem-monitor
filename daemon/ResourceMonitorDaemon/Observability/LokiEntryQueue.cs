using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Bounded in-memory queue between the sources (logger, metrics, device status) and the
    /// Loki sender. Never blocks a producer: when full, the oldest entry is dropped and counted.
    /// Timestamps are assigned under the queue lock, so queue order equals timestamp order.
    /// </summary>
    /// <remarks>
    /// It has no dependencies so the <see cref="LokiLoggerProvider"/> can be created by the
    /// logging framework before the rest of the pipeline exists. Until <see cref="LokiSink.Configure"/>
    /// runs it accepts (and bounds) early start-up entries; a disabled sink stops and clears it.
    /// Thread-safe.
    /// </remarks>
    public sealed class LokiEntryQueue
    {
        /// <summary>Longer lines are truncated (Loki's default line limit is far higher; this bounds memory).</summary>
        public const int MaxLineLength = 16 * 1024;

        private readonly object _gate = new();
        private readonly Queue<LokiEntry> _entries = new();
        private readonly MonotonicNanoClock _clock;
        private int _capacity;
        private int _batchThreshold = LokiSettings.DefaultBatchMaxEntries;
        private long _dropped;
        private long _totalDropped;
        private volatile bool _accepting = true;
        private volatile int _minLogLevel = (int)LogLevel.Information;

        public LokiEntryQueue()
            : this(LokiSettings.DefaultQueueCapacity)
        {
        }

        public LokiEntryQueue(int capacity, MonotonicNanoClock? clock = null)
        {
            _capacity = Math.Max(1, capacity);
            _clock = clock ?? MonotonicNanoClock.Shared;
        }

        /// <summary>Raised (outside the lock, on the producer's thread) when the count reaches the batch threshold.</summary>
        public Action? ThresholdReached { get; set; }

        /// <summary>False while the sink is disabled: <see cref="TryEnqueue"/> then does nothing.</summary>
        public bool Accepting
        {
            get => _accepting;
            set => _accepting = value;
        }

        /// <summary>Minimum level of daemon log lines (used by <see cref="LokiLoggerProvider"/>).</summary>
        public LogLevel MinLogLevel
        {
            get => (LogLevel)_minLogLevel;
            set => _minLogLevel = (int)value;
        }

        public int Capacity
        {
            get
            {
                lock (_gate)
                {
                    return _capacity;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>Entries dropped since the queue was created.</summary>
        public long TotalDropped => Interlocked.Read(ref _totalDropped);

        /// <summary>Count at which <see cref="ThresholdReached"/> fires (the batch size).</summary>
        public int BatchThreshold
        {
            get => Volatile.Read(ref _batchThreshold);
            set => Volatile.Write(ref _batchThreshold, Math.Max(1, value));
        }

        /// <summary>Changes the capacity; when shrinking, the oldest entries are dropped and counted.</summary>
        public void SetCapacity(int capacity)
        {
            lock (_gate)
            {
                _capacity = Math.Max(1, capacity);
                while (_entries.Count > _capacity)
                {
                    _entries.Dequeue();
                    CountDropLocked();
                }
            }
        }

        /// <summary>
        /// Adds a line with the next process-wide timestamp. Returns false when not accepting.
        /// Drops the oldest entry if full.
        /// </summary>
        public bool TryEnqueue(LokiStreamKind kind, string? level, string line)
        {
            if (!_accepting)
            {
                return false;
            }

            if (line.Length > MaxLineLength)
            {
                line = line[..MaxLineLength] + "…(truncated)";
            }

            bool signal;
            lock (_gate)
            {
                if (_entries.Count >= _capacity)
                {
                    _entries.Dequeue();
                    CountDropLocked();
                }

                _entries.Enqueue(new LokiEntry(kind, level, _clock.Next(), line));
                signal = _entries.Count == BatchThreshold;
            }

            if (signal)
            {
                ThresholdReached?.Invoke();
            }

            return true;
        }

        /// <summary>Removes and returns up to <paramref name="max"/> of the oldest entries.</summary>
        public List<LokiEntry> DequeueBatch(int max)
        {
            lock (_gate)
            {
                var batch = new List<LokiEntry>(Math.Min(Math.Max(0, max), _entries.Count));
                while (batch.Count < max && _entries.TryDequeue(out var entry))
                {
                    batch.Add(entry);
                }

                return batch;
            }
        }

        /// <summary>Returns the drops since the previous call and resets that counter.</summary>
        public long TakeDroppedCount() => Interlocked.Exchange(ref _dropped, 0);

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        private void CountDropLocked()
        {
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _totalDropped);
        }
    }
}
