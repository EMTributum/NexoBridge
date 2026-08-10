using NexoBridge.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public sealed class RcpImportJobQueue
    {
        private readonly Channel<RcpImportJob> _queue;

        public RcpImportJobQueue()
        {
            var options = new UnboundedChannelOptions { SingleReader = true };
            _queue = Channel.CreateUnbounded<RcpImportJob>(options);
        }

        public async ValueTask QueueJobAsync(RcpImportJob job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<RcpImportJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
