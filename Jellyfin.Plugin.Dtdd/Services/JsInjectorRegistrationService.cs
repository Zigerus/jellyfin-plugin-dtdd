using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// IHostedService that registers <c>Web/dtdd-injector.js</c> with the
/// JavaScript Injector plugin on Jellyfin startup. Uses the reflection
/// bridge in <see cref="JsInjectorBridge"/> — JS Injector isn't a NuGet
/// dependency, so we discover its assembly at runtime.
///
/// <para>
/// <b>Startup ordering</b> — Jellyfin loads all plugins before starting
/// IHostedService instances, so JS Injector's <c>Plugin.Instance</c> and
/// its DI services are ready by the time this fires. We still apply a
/// small <see cref="StartupDelay"/> as belt-and-braces.
/// </para>
///
/// <para>
/// <b>Stale-registration cleanup</b> — calls
/// <see cref="JsInjectorBridge.TryUnregisterAll"/> before registering, so
/// dev sideloads that hot-reload the plugin DLL don't leave a previous
/// script registration lingering.
/// </para>
///
/// <para>
/// <b>Embedded resource</b> — dtdd-injector.js is built into the plugin
/// assembly as an embedded resource via the csproj. This service reads it
/// from <see cref="Assembly.GetManifestResourceStream"/> and sends the
/// content as the <c>script</c> field of the RegisterScript payload.
/// </para>
/// </summary>
public class JsInjectorRegistrationService : IHostedService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);
    private const string ScriptResourceSuffix = "dtdd-injector.js";

    private readonly ILogger<JsInjectorRegistrationService> _logger;

    public JsInjectorRegistrationService(ILogger<JsInjectorRegistrationService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("Plugin.Instance is null during JS Injector registration — skipping");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        // Clear any stale registration from a prior load before registering this one.
        JsInjectorBridge.TryUnregisterAll(plugin.Id, _logger);

        var scriptContent = LoadEmbeddedScript();
        if (scriptContent is null)
        {
            _logger.LogWarning(
                "Embedded resource {Resource} not found; DTDD badge UI will not render",
                ScriptResourceSuffix);
            return;
        }

        JsInjectorBridge.TryRegisterScript(
            scriptId: $"{plugin.Id:N}-main",
            scriptName: $"{plugin.Name} (badge + picker)",
            script: scriptContent,
            pluginId: plugin.Id,
            pluginName: plugin.Name,
            pluginVersion: plugin.Version?.ToString() ?? "0.0.0.0",
            logger: _logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string? LoadEmbeddedScript()
    {
        var assembly = typeof(JsInjectorRegistrationService).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ScriptResourceSuffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
