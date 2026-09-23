using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class RawPayrollCountsJobQueue
    {
        private readonly Channel<RawPayrollCountsBatchJob> _queue;

        public RawPayrollCountsJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<RawPayrollCountsBatchJob>(options);
        }

        public async ValueTask QueueJobAsync(RawPayrollCountsBatchJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<RawPayrollCountsBatchJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
