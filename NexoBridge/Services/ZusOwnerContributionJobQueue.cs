using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class ZusOwnerContributionJobQueue
    {
        private readonly Channel<ZusOwnerContributionBatchJob> _queue;

        public ZusOwnerContributionJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<ZusOwnerContributionBatchJob>(options);
        }

        public async ValueTask QueueJobAsync(ZusOwnerContributionBatchJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<ZusOwnerContributionBatchJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
