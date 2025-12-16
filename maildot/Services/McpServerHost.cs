using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using maildot.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace maildot.Services;

public sealed class McpServerHost
{
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private WebApplication? _app;

    public bool IsRunning => _app != null;

    public async Task TryStartAsync(McpSettings settings)
    {
        if (!settings.Enabled)
        {
            return;
        }

        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _runTask = RunServerAsync(settings, _cts.Token);
        await Task.CompletedTask;
    }

    public async Task TryRestartAsync(McpSettings settings)
    {
        await StopAsync();
        await TryStartAsync(settings);
    }

    public async Task StopAsync()
    {
        if (_cts == null && _app == null && _runTask == null)
        {
            return;
        }

        try
        {
            _cts?.Cancel();

            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }

            if (_runTask != null)
            {
                await _runTask;
            }
        }
        catch
        {
            // swallow shutdown errors
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _runTask = null;
            _app = null;
        }
    }

    private async Task RunServerAsync(McpSettings settings, CancellationToken token)
    {
        try
        {
            var url = $"http://{settings.BindAddress}:{settings.Port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = []
            });

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly(typeof(McpServerHost).Assembly);

            _app = builder.Build();

            _app.Use(async (context, next) =>
            {
                if (context.Request.Headers.TryGetValue("Origin", out var origins) && origins.Count > 0)
                {
                    var allowed = origins.Any(origin => IsOriginAllowed(origin, settings.BindAddress));
                    if (!allowed)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Forbidden origin");
                        return;
                    }
                }

                await next();
            });

            _app.Urls.Add(url);
            _app.MapMcp();
            _app.MapGet("/health", () => new { status = "ok", timestamp = DateTime.UtcNow });

            await _app.RunAsync(token);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP server host error: {ex}");
        }
    }

    private static bool IsOriginAllowed(string? origin, string bindAddress)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (IPAddress.TryParse(bindAddress, out var bindIp))
        {
            if (IPAddress.IsLoopback(bindIp))
            {
                return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                    IPAddress.TryParse(uri.Host, out var originIp) && IPAddress.IsLoopback(originIp);
            }

            return string.Equals(uri.Host, bindAddress, StringComparison.OrdinalIgnoreCase);
        }

        // fallback to simple host compare; allow localhost for common case
        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(uri.Host, bindAddress, StringComparison.OrdinalIgnoreCase);
    }
}
