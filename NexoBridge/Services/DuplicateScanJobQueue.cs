using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class DuplicateScanJobQueue
    {
        private readonly Channel<YearlyDuplicateScanJob> _queue;

        public DuplicateScanJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<YearlyDuplicateScanJob>(options);
        }

        public async ValueTask QueueJobAsync(YearlyDuplicateScanJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<YearlyDuplicateScanJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
