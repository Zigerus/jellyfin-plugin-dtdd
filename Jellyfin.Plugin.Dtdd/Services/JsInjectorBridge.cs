using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// Reflection bridge to <c>n00bcodr/Jellyfin-JavaScript-Injector</c>'s
/// <c>Jellyfin.Plugin.JavaScriptInjector.PluginInterface</c> static API.
///
/// <para>
/// We can't add a typed compile-time reference to that plugin (it isn't on
/// NuGet and we don't want a hard load-order dependency). Instead we walk
/// <see cref="AssemblyLoadContext.All"/> at runtime, locate the assembly,
/// resolve the PluginInterface type by name, and invoke its static methods.
/// </para>
///
/// <para>
/// Graceful degradation: if JS Injector isn't installed, every method here
/// returns false/0 with a single WARN log. The backend API continues to
/// function — only the badge UI is missing.
/// </para>
/// </summary>
internal static class JsInjectorBridge
{
    private const string JsInjectorAssemblyMarker = "Jellyfin.Plugin.JavaScriptInjector";
    private const string PluginInterfaceTypeName = "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";

    /// <summary>
    /// Register a script with JS Injector. The payload follows JS Injector's
    /// PluginInterface.RegisterScript documented contract (see Phase 0 findings).
    /// </summary>
    public static bool TryRegisterScript(
        string scriptId,
        string scriptName,
        string script,
        Guid pluginId,
        string pluginName,
        string pluginVersion,
        ILogger logger)
    {
        var registerMethod = FindMethod("RegisterScript", logger);
        if (registerMethod is null)
        {
            return false;
        }

        var payload = new JObject
        {
            ["id"] = scriptId,
            ["name"] = scriptName,
            ["script"] = script,
            ["enabled"] = true,
            ["requiresAuthentication"] = true,
            ["pluginId"] = pluginId.ToString(),
            ["pluginName"] = pluginName,
            ["pluginVersion"] = pluginVersion,
        };

        try
        {
            var result = registerMethod.Invoke(null, new object[] { payload });
            if (result is bool ok && ok)
            {
                logger.LogInformation("Registered DTDD injector script with JavaScriptInjector (id={ScriptId})", scriptId);
                return true;
            }

            logger.LogWarning("JavaScriptInjector.RegisterScript returned false for {ScriptId}", scriptId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception calling JavaScriptInjector.RegisterScript for {ScriptId}", scriptId);
            return false;
        }
    }

    /// <summary>
    /// Remove every script registered by this plugin. Used on uninstall and
    /// on startup (to clear stale registrations from a previous load).
    /// </summary>
    public static int TryUnregisterAll(Guid pluginId, ILogger logger)
    {
        var method = FindMethod("UnregisterAllScriptsFromPlugin", logger);
        if (method is null)
        {
            return 0;
        }

        try
        {
            var result = method.Invoke(null, new object[] { pluginId.ToString() });
            if (result is int n)
            {
                return n;
            }
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception calling JavaScriptInjector.UnregisterAllScriptsFromPlugin for {PluginId}", pluginId);
            return 0;
        }
    }

    private static MethodInfo? FindMethod(string methodName, ILogger logger)
    {
        var assembly = FindAssembly();
        if (assembly is null)
        {
            // Single, friendly note — install/restart is the user fix.
            logger.LogWarning(
                "JavaScriptInjector plugin not loaded. The DTDD backend will work but the badge UI will not render. " +
                "Install n00bcodr/Jellyfin-JavaScript-Injector and restart Jellyfin.");
            return null;
        }

        var type = assembly.GetType(PluginInterfaceTypeName);
        if (type is null)
        {
            logger.LogWarning("JavaScriptInjector loaded but {Type} not found", PluginInterfaceTypeName);
            return null;
        }

        var method = type.GetMethod(methodName);
        if (method is null)
        {
            logger.LogWarning("JavaScriptInjector.{Method} not found", methodName);
            return null;
        }

        return method;
    }

    private static Assembly? FindAssembly()
    {
        return AssemblyLoadContext.All
            .SelectMany(ctx => ctx.Assemblies)
            .FirstOrDefault(a => a.FullName is not null && a.FullName.Contains(JsInjectorAssemblyMarker, StringComparison.Ordinal));
    }
}
