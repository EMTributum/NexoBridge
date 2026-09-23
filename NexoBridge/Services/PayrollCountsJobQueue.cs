using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class PayrollCountsJobQueue
    {
        private readonly Channel<PayrollCountsBatchJob> _queue;

        public PayrollCountsJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<PayrollCountsBatchJob>(options);
        }

        public async ValueTask QueueJobAsync(PayrollCountsBatchJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<PayrollCountsBatchJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
