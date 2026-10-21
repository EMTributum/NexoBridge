using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class TaxPreviewJobQueue
    {
        private readonly Channel<TaxPreviewBatchJob> _queue;

        public TaxPreviewJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<TaxPreviewBatchJob>(options);
        }

        public async ValueTask QueueJobAsync(TaxPreviewBatchJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<TaxPreviewBatchJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
