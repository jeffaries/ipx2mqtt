using Ipx2Mqtt.Ipx;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ipx2Mqtt.Service;

/// <summary>The configuration web interface: static Vue/Vuetify files (wwwroot) and a small JSON API.</summary>
public static class WebInterface
{
    public static void UseWebInterface(this WebApplication app, WebOptions web, PushOptions push)
    {
        // Everything except the Push endpoint belongs to the web interface: it is served on Web.Port only and,
        // when a password is set, behind HTTP Basic authentication.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/push"))
            {
                await next();
                return;
            }

            if (push.Enabled && web.Port != push.Port && context.Connection.LocalPort != web.Port)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!string.IsNullOrEmpty(web.Password) && !HasValidCredentials(context.Request, web))
            {
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"ipx2mqtt\", charset=\"UTF-8\"";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapHub<MonitorHub>("/hubs/monitor");

        app.MapGet("/api/config", (ConfigEditor editor) => Results.Json(editor.Read()));

        app.MapPut("/api/config", (JsonObject body, ConfigEditor editor) =>
        {
            if (body["config"] is not JsonObject config || body["entities"] is not JsonArray entities)
            {
                return Results.BadRequest(new { errors = new[] { "Expected { \"config\": {...}, \"entities\": [...] }." } });
            }

            try
            {
                var errors = editor.Save(config, entities);
                return errors.Count == 0 ? Results.Ok(new { saved = true }) : Results.BadRequest(new { errors });
            }
            catch (IOException ex)
            {
                return Results.Json(new { errors = new[] { $"Cannot write the configuration: {ex.Message}" } }, statusCode: 500);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { errors = new[] { $"Cannot write the configuration (is the folder mounted read-only?): {ex.Message}" } }, statusCode: 500);
            }
        });

        // The container restart policy (restart: unless-stopped) brings the service back with the new files.
        app.MapPost("/api/restart", (IHostApplicationLifetime lifetime) =>
        {
            _ = Task.Delay(500).ContinueWith(_ => lifetime.StopApplication());
            return Results.Accepted();
        });
    }

    private static bool HasValidCredentials(HttpRequest request, WebOptions web)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var expected = Encoding.UTF8.GetBytes($"{(string.IsNullOrEmpty(web.Username) ? "admin" : web.Username)}:{web.Password}");
            var given = Convert.FromBase64String(header["Basic ".Length..].Trim());
            return CryptographicOperations.FixedTimeEquals(given, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
