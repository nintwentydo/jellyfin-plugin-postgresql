using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Postgresql.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Postgresql;

/// <summary>
/// The PostgreSQL database provider plugin.
/// </summary>
/// <remarks>
/// The database provider itself is not resolved through the plugin framework: Jellyfin loads it
/// directly with <c>Assembly.LoadFrom</c> during service registration, before plugins are
/// initialised, because the database has to exist before anything else can start. This type only
/// gives the assembly an identity in the dashboard and hosts the configuration page.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "PostgreSQL";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7baa1c73-ffc3-4613-a173-51dbd085c4f5");

    /// <inheritdoc />
    public override string Description => "Runs Jellyfin's database on PostgreSQL instead of SQLite.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}
