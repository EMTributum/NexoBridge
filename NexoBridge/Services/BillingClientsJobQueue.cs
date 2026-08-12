using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class BillingClientsJobQueue
    {
        private readonly Channel<BillingClientsJob> _queue;

        public BillingClientsJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<BillingClientsJob>(options);
        }

        public async ValueTask QueueJobAsync(BillingClientsJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<BillingClientsJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
