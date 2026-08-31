using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Postgresql.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
/// <remarks>
/// Connection settings deliberately do not live here. Jellyfin resolves the database provider
/// during service registration, long before plugin configuration is available, so the connection
/// has to come from <c>database.xml</c> or the environment. This type exists only because
/// <c>BasePlugin&lt;T&gt;</c> requires one; the dashboard page it anchors shows static setup
/// instructions.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
}
