using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class InvoiceCreationJobQueue
    {
        private readonly Channel<InvoiceCreationJob> _queue;

        public InvoiceCreationJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<InvoiceCreationJob>(options);
        }

        public async ValueTask QueueJobAsync(InvoiceCreationJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<InvoiceCreationJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
