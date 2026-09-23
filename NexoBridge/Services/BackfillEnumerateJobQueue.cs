using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class BackfillEnumerateJobQueue
    {
        private readonly Channel<BackfillEnumerateJob> _queue;

        public BackfillEnumerateJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<BackfillEnumerateJob>(options);
        }

        public async ValueTask QueueJobAsync(BackfillEnumerateJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<BackfillEnumerateJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
