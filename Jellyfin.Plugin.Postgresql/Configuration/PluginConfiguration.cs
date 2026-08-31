using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Postgresql.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
/// <remarks>
/// Connection settings deliberately do not live here. Jellyfin resolves the database provider
/// during service registration, long before plugin configuration is available, so the connection
/// has to come from <c>database.xml</c> or the environment. This type exists so the plugin has a
/// configuration page in the dashboard that can report the live connection state.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
}
