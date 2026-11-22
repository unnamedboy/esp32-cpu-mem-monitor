namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Pure reconnect schedule: after each failure the next attempt is delayed by the current
    /// delay, which then doubles up to the maximum. A successful connect does not reset the
    /// schedule by itself: only a connection that stays up for at least
    /// <see cref="StableConnectionThreshold"/> does, so a broker that accepts and immediately
    /// drops connections is still backed off. <see cref="Reset"/> (endpoint change) makes the
    /// next attempt due immediately and restores the initial delay.
    /// Not thread-safe; callers synchronize.
    /// </summary>
    public sealed class ReconnectBackoff
    {
        private readonly Func<DateTimeOffset> _clock;
        private DateTimeOffset? _connectedAt;

        public ReconnectBackoff(TimeSpan initialDelay, TimeSpan maxDelay, Func<DateTimeOffset>? clock = null)
        {
            if (initialDelay <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be positive.");
            }

            InitialDelay = initialDelay;
            MaxDelay = maxDelay < initialDelay ? initialDelay : maxDelay;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            Reset();
        }

        public TimeSpan InitialDelay { get; }

        public TimeSpan MaxDelay { get; }

        /// <summary>Delay that the next <see cref="RecordFailure"/> will apply.</summary>
        public TimeSpan NextDelay { get; private set; }

        /// <summary>Earliest time the next attempt may start.</summary>
        public DateTimeOffset NextAttemptAt { get; private set; }

        /// <summary>Failures since the last <see cref="Reset"/>.</summary>
        public int ConsecutiveFailures { get; private set; }

        public bool IsAttemptDue => _clock() >= NextAttemptAt;

        /// <summary>How long a connection must last before the schedule resets (= max delay).</summary>
        public TimeSpan StableConnectionThreshold => MaxDelay;

        /// <summary>Records a successful connect (starts the stability timer).</summary>
        public void RecordConnected() => _connectedAt = _clock();

        /// <summary>
        /// Records that an established connection dropped. A stable connection resets the
        /// schedule (returns <c>null</c>, next attempt due now); a short-lived one counts as a
        /// failure and returns the delay until the next attempt.
        /// </summary>
        public TimeSpan? RecordDisconnected()
        {
            var stable = _connectedAt is { } connectedAt && _clock() - connectedAt >= StableConnectionThreshold;
            _connectedAt = null;
            if (stable)
            {
                Reset();
                return null;
            }

            return RecordFailure();
        }

        /// <summary>Records a failed attempt and returns the delay until the next one.</summary>
        public TimeSpan RecordFailure()
        {
            var delay = NextDelay;
            ConsecutiveFailures++;
            NextAttemptAt = _clock() + delay;
            NextDelay = delay >= MaxDelay / 2 ? MaxDelay : delay * 2;
            return delay;
        }

        public void Reset()
        {
            _connectedAt = null;
            ConsecutiveFailures = 0;
            NextDelay = InitialDelay;
            NextAttemptAt = DateTimeOffset.MinValue;
        }
    }
}
