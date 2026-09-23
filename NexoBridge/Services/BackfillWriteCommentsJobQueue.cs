using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class BackfillWriteCommentsJobQueue
    {
        private readonly Channel<BackfillWriteCommentsJob> _queue;

        public BackfillWriteCommentsJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<BackfillWriteCommentsJob>(options);
        }

        public async ValueTask QueueJobAsync(BackfillWriteCommentsJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<BackfillWriteCommentsJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
