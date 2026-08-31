using System;
using Jellyfin.Database.Implementations;

namespace Jellyfin.Plugin.Postgresql.Tests;

/// <summary>
/// Owns a <see cref="JellyfinDbContext"/> built from the design-time factory. The context is never
/// connected: the assertions read the model and the SQL EF would generate.
/// </summary>
internal sealed class JellyfinDbContextFixture : IDisposable
{
    public JellyfinDbContextFixture(JellyfinDbContext context)
    {
        Context = context;
    }

    public JellyfinDbContext Context { get; }

    public void Dispose() => Context.Dispose();
}
