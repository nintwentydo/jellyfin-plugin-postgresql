using System;
using Jellyfin.Plugin.Postgresql.Database;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Postgresql.Tests;

[CollectionDefinition("Native tool environment", DisableParallelization = true)]
public class NativeToolEnvironmentCollection;

[Collection("Native tool environment")]
public class PostgresqlToolSettingsTests
{
    [Theory]
    [InlineData(SslMode.Disable, "disable")]
    [InlineData(SslMode.Allow, "allow")]
    [InlineData(SslMode.Prefer, "prefer")]
    [InlineData(SslMode.Require, "require")]
    [InlineData(SslMode.VerifyCA, "verify-ca")]
    [InlineData(SslMode.VerifyFull, "verify-full")]
    public void Explicit_ssl_mode_overrides_the_tools_environment(SslMode mode, string expected)
    {
        var previous = Environment.GetEnvironmentVariable("PGSSLMODE");
        try
        {
            Environment.SetEnvironmentVariable("PGSSLMODE", "inherited-mode");
            var connection = new NpgsqlConnectionStringBuilder { SslMode = mode };
            var startInfo = PostgresqlConnectionSettings.CreateToolStartInfo("psql", [], connection);

            Assert.Equal(expected, startInfo.Environment["PGSSLMODE"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGSSLMODE", previous);
        }
    }

    [Fact]
    public void Unspecified_tls_settings_preserve_existing_libpq_configuration()
    {
        var previousMode = Environment.GetEnvironmentVariable("PGSSLMODE");
        var previousCertificate = Environment.GetEnvironmentVariable("PGSSLROOTCERT");
        try
        {
            Environment.SetEnvironmentVariable("PGSSLMODE", "verify-full");
            Environment.SetEnvironmentVariable("PGSSLROOTCERT", "/existing/root.crt");
            var startInfo = PostgresqlConnectionSettings.CreateToolStartInfo("pg_dump", [], new NpgsqlConnectionStringBuilder());

            Assert.Equal("verify-full", startInfo.Environment["PGSSLMODE"]);
            Assert.Equal("/existing/root.crt", startInfo.Environment["PGSSLROOTCERT"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGSSLMODE", previousMode);
            Environment.SetEnvironmentVariable("PGSSLROOTCERT", previousCertificate);
        }
    }

    [Fact]
    public void Tools_keep_their_client_certificate_when_npgsql_uses_a_different_format()
    {
        var previousCertificate = Environment.GetEnvironmentVariable("PGSSLCERT");
        var previousKey = Environment.GetEnvironmentVariable("PGSSLKEY");
        try
        {
            Environment.SetEnvironmentVariable("PGSSLCERT", "/libpq/client.pem");
            Environment.SetEnvironmentVariable("PGSSLKEY", "/libpq/client.key");
            var connection = new NpgsqlConnectionStringBuilder
            {
                SslCertificate = "/npgsql/client.pfx",
                SslKey = "/npgsql/client.key"
            };
            var startInfo = PostgresqlConnectionSettings.CreateToolStartInfo("pg_dump", [], connection);

            Assert.Equal("/libpq/client.pem", startInfo.Environment["PGSSLCERT"]);
            Assert.Equal("/libpq/client.key", startInfo.Environment["PGSSLKEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGSSLCERT", previousCertificate);
            Environment.SetEnvironmentVariable("PGSSLKEY", previousKey);
        }
    }

    [Fact]
    public void Tool_arguments_and_certificate_paths_preserve_literal_values_without_exposing_passwords()
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "db.example",
            Username = "user with spaces",
            Database = "database with spaces",
            Password = "test-secret",
            RootCertificate = "/certificates with spaces/root.crt"
        };
        const string FileArgument = "--file=/backup with spaces/quoted\"name.sql";
        var startInfo = PostgresqlConnectionSettings.CreateToolStartInfo("psql", [FileArgument], connection);

        Assert.Contains(FileArgument, startInfo.ArgumentList);
        Assert.Contains("--username=user with spaces", startInfo.ArgumentList);
        Assert.Contains("--dbname=database with spaces", startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
        Assert.DoesNotContain("test-secret", string.Join(' ', startInfo.ArgumentList), StringComparison.Ordinal);
        Assert.Equal("test-secret", startInfo.Environment["PGPASSWORD"]);
        Assert.Equal(connection.RootCertificate, startInfo.Environment["PGSSLROOTCERT"]);
    }
}
