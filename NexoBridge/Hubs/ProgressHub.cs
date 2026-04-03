using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace NexoBridge.Hubs
{
    public class ProgressHub : Hub
    {
        // Klienci wywołają tę metodę zaraz po połączeniu, 
        // aby nasłuchiwać tylko wiadomości z własnego zadania.
        public async Task SubscribeToJob(string jobId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
        }
    }
}