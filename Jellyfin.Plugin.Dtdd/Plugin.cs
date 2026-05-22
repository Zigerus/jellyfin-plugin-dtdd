using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Dtdd.Configuration;
using Jellyfin.Plugin.Dtdd.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
    }

    public override string Name => "DoesTheDogDie";

    public override Guid Id => Guid.Parse("4479e434-651e-48f7-a2ee-bec0bdadec5e");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "DoesTheDogDie",
                EnableInMainMenu = true,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        };
    }

    /// <summary>
    /// Tear down our JS Injector registration when the plugin is uninstalled so
    /// the badge JS doesn't linger after the backend is gone. Failures are
    /// logged and swallowed — uninstall must complete cleanly.
    /// </summary>
    public override void OnUninstalling()
    {
        try
        {
            var removed = JsInjectorBridge.TryUnregisterAll(Id, _logger);
            if (removed > 0)
            {
                _logger.LogInformation("Removed {Count} DTDD script registration(s) from JavaScriptInjector on uninstall", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister DTDD scripts from JavaScriptInjector during uninstall");
        }

        base.OnUninstalling();
    }
}
