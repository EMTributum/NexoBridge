using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class OfficeVatFlagsJobQueue
    {
        private readonly Channel<OfficeVatFlagsJob> _queue;

        public OfficeVatFlagsJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<OfficeVatFlagsJob>(options);
        }

        public async ValueTask QueueJobAsync(OfficeVatFlagsJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<OfficeVatFlagsJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
