using System;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Builds a <see cref="JellyfinDbContext"/> for <c>dotnet ef</c>. Not used at runtime.
/// </summary>
/// <remarks>
/// This goes through <see cref="PostgresqlDatabaseProvider.Initialise"/> rather than calling
/// <c>UseNpgsql</c> directly so that scaffolding sees the same model the server will: the
/// collation and UTC mapping in <see cref="PostgresqlModelCustomizer"/> are registered there, and
/// a migration generated without them would not match the running schema.
/// </remarks>
internal sealed class PostgresqlDesignTimeJellyfinDbFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    /// <summary>
    /// No server is contacted when scaffolding a migration, so these credentials only need to
    /// parse. <c>dotnet ef database update</c> overrides them with <c>--connection</c>.
    /// </summary>
    private const string ScaffoldingConnectionString =
        "Host=localhost;Port=5432;Database=jellyfin;Username=jellyfin;Password=jellyfin";

    /// <inheritdoc />
    public JellyfinDbContext CreateDbContext(string[] args)
    {
        var provider = new PostgresqlDatabaseProvider(
            null!,
            NullLogger<PostgresqlDatabaseProvider>.Instance);

        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();

        provider.Initialise(
            optionsBuilder,
            new DatabaseConfigurationOptions
            {
                DatabaseType = "PLUGIN_PROVIDER",
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = "PostgreSQL",
                    PluginAssembly = "Jellyfin.Plugin.Postgresql.dll",
                    ConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                        ?? ScaffoldingConnectionString
                }
            });

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
