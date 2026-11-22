namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Pure reconnect schedule: after each failure the next attempt is delayed by the current
    /// delay, which then doubles up to the maximum. <see cref="Reset"/> (on success or endpoint
    /// change) makes the next attempt due immediately and restores the initial delay.
    /// Not thread-safe; callers synchronize.
    /// </summary>
    public sealed class ReconnectBackoff
    {
        private readonly Func<DateTimeOffset> _clock;

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
            ConsecutiveFailures = 0;
            NextDelay = InitialDelay;
            NextAttemptAt = DateTimeOffset.MinValue;
        }
    }
}
