using NexoBridge.Models;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading;

namespace NexoBridge.Services
{
    public class JobQueue
    {
        private readonly Channel<ImportJob> _queue;

        public JobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<ImportJob>(options);
        }

        public async ValueTask QueueJobAsync(ImportJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<ImportJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}