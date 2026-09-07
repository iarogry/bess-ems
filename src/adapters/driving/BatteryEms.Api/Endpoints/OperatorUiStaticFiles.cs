using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace BatteryEms.Api.Endpoints;

public static class OperatorUiStaticFiles
{
    public static WebApplication UseOperatorUiStaticShell(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect("/operator/");
                return;
            }

            if (context.Request.Path.Equals("/operator", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect("/operator/");
                return;
            }

            await next().ConfigureAwait(false);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                if (!context.Context.Request.Path.StartsWithSegments("/operator", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Context.Response.Headers.Pragma = "no-cache";
                context.Context.Response.Headers.Expires = "0";
            },
        });
        return app;
    }
}
