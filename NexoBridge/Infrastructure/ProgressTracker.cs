using System;
using System.Threading.Tasks;

namespace NexoBridge.Infrastructure
{
    public sealed class ProgressTracker
    {
        private readonly object _sync = new object();
        private readonly Func<int, string, Task> _sendAsync;
        private readonly int _progressLimit;
        private int _progress;
        private int _lastPercent;

        public ProgressTracker(Func<int, string, Task> sendAsync, int progressLimit)
        {
            _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
            _progressLimit = Math.Max(1, progressLimit);
        }

        public ProgressSegment BeginSegment(int units)
        {
            lock (_sync)
            {
                return new ProgressSegment(this, _progress, Math.Max(0, units));
            }
        }

        public Task AdvanceAsync(int units, string message)
        {
            int target;
            lock (_sync)
            {
                target = _progress + Math.Max(0, units);
            }

            return SetUnitsAsync(target, message);
        }

        public Task CompleteAsync(string message)
        {
            return SetUnitsAsync(_progressLimit, message);
        }

        private Task SetUnitsAsync(int units, string message)
        {
            int percent;
            lock (_sync)
            {
                _progress = Math.Max(_progress, Math.Min(_progressLimit, units));
                percent = (int)Math.Round(_progress * 100m / _progressLimit, MidpointRounding.AwayFromZero);
                percent = Math.Max(_lastPercent, Math.Min(100, percent));
                _lastPercent = percent;
            }

            return _sendAsync(percent, message);
        }

        public sealed class ProgressSegment
        {
            private readonly ProgressTracker _tracker;
            private readonly int _start;
            private readonly int _units;
            private int _lastLocalPercent;

            internal ProgressSegment(ProgressTracker tracker, int start, int units)
            {
                _tracker = tracker;
                _start = start;
                _units = units;
            }

            public Task ReportAsync(int localPercent, string message)
            {
                localPercent = Math.Max(_lastLocalPercent, Math.Min(100, localPercent));
                _lastLocalPercent = localPercent;
                int target = _start + (int)Math.Round(_units * localPercent / 100m, MidpointRounding.AwayFromZero);
                return _tracker.SetUnitsAsync(target, message);
            }

            public void ReportSync(int localPercent, string message)
            {
                ReportAsync(localPercent, message).GetAwaiter().GetResult();
            }

            public Task CompleteAsync(string message)
            {
                _lastLocalPercent = 100;
                return _tracker.SetUnitsAsync(_start + _units, message);
            }
        }
    }
}
