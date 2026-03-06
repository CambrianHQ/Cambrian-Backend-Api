using Cambrian.Api.Middleware;

namespace Cambrian.Api.Extensions;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseCambrianApi(this IApplicationBuilder app)
    {
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        return app;
    }
}
