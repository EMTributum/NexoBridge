using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class BillingJobQueue
    {
        private readonly Channel<BillingSnapshotJob> _queue;

        public BillingJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<BillingSnapshotJob>(options);
        }

        public async ValueTask QueueJobAsync(BillingSnapshotJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<BillingSnapshotJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
