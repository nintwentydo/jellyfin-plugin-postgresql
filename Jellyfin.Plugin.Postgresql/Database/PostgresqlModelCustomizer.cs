using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Applies PostgreSQL-specific mapping to Jellyfin's shared entity model.
/// </summary>
/// <remarks>
/// This runs as an <see cref="IModelCustomizer"/> rather than from
/// <c>IJellyfinDatabaseProvider.OnModelCreating</c> because Jellyfin invokes that hook before
/// <c>ApplyConfigurationsFromAssembly</c>, so the model is still incomplete there and a walk over
/// its properties would miss most of them.
/// </remarks>
internal sealed class PostgresqlModelCustomizer : RelationalModelCustomizer
{
    private static readonly ValueConverter<DateTime, DateTime> _utcConverter = new(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresqlModelCustomizer"/> class.
    /// </summary>
    /// <param name="dependencies">Dependencies supplied by EF Core.</param>
    public PostgresqlModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.Customize(modelBuilder, context);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = property.ClrType;

                if (clrType == typeof(string))
                {
                    // SQLite compares text with BINARY (byte ordinal). PostgreSQL would otherwise
                    // use the database's LC_COLLATE, which on a typical en_US.UTF-8 cluster ignores
                    // punctuation and spaces at the primary level, silently reshuffling SortName
                    // ordering and the A-Z jump bar relative to every other Jellyfin install.
                    // Setting this per column also keeps it independent of how the DBA created the
                    // database, which a model-level UseCollation would not.
                    property.SetCollation("C");
                }
                else if ((clrType == typeof(DateTime) || clrType == typeof(DateTime?))
                         && property.GetValueConverter() is null)
                {
                    // Npgsql maps DateTime to `timestamp with time zone` and throws on any value
                    // whose Kind is not Utc. Jellyfin stores UTC throughout but does not guarantee
                    // Kind on the way in or out.
                    property.SetValueConverter(_utcConverter);
                }
            }
        }
    }
}
