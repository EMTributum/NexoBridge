using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NexoBridge.API
{
    public static class HealthEndpoints
    {
        public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/ping", () => Results.Ok(new { Status = "Online" }));
            return app;
        }
    }
}
