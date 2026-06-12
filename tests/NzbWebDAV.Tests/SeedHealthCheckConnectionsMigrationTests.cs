using System.Text.Json;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Migrations;

namespace NzbWebDAV.Tests;

public class SeedHealthCheckConnectionsMigrationTests
{
    // Type 1 = Pooled, 2 = BackupAndStats, 3 = BackupOnly. Pass on the first provider
    // carries a quote and a backslash to prove JSON escaping survives the rebuild.
    private const string ProvidersJson = """
        {"Providers":[
          {"Type":1,"Host":"news.a.com","Port":563,"UseSsl":true,"User":"u1","Pass":"p\"a\\ss","MaxConnections":15},
          {"Type":2,"Host":"news.b.com","Port":119,"UseSsl":false,"User":"u2","Pass":"pw2","MaxConnections":6},
          {"Type":3,"Host":"news.c.com","Port":563,"UseSsl":true,"User":"u3","Pass":"pw3","MaxConnections":20},
          {"Type":1,"Host":"news.d.com","Port":563,"UseSsl":true,"User":"u4","Pass":"pw4","MaxConnections":5},
          {"Type":1,"Host":"news.e.com","Port":563,"UseSsl":true,"User":"u5","Pass":"pw5","MaxConnections":30,"HealthCheckConnections":9}
        ]}
        """;

    [Fact]
    public void Up_CarvesThreeStreamingConnectionsIntoHealthChecksForEligibleProviders()
    {
        var providers = RunSql(ProvidersJson, SeedHealthCheckConnectionsFromStreaming.UpSql).Providers;

        // Pooled, can spare 3 → carved.
        AssertProvider(providers[0], maxConnections: 12, healthCheckConnections: 3);
        // BackupAndStats with exactly the minimum (6) → carved to 3 + 3.
        AssertProvider(providers[1], maxConnections: 3, healthCheckConnections: 3);
        // BackupOnly → never seeded.
        AssertProvider(providers[2], maxConnections: 20, healthCheckConnections: 0);
        // Pooled but can't spare 3 (5 < 6) → untouched.
        AssertProvider(providers[3], maxConnections: 5, healthCheckConnections: 0);
        // Already configured (9) → untouched.
        AssertProvider(providers[4], maxConnections: 30, healthCheckConnections: 9);
    }

    [Fact]
    public void Up_PreservesEscapedCredentialsAndSslFlags()
    {
        var providers = RunSql(ProvidersJson, SeedHealthCheckConnectionsFromStreaming.UpSql).Providers;

        Assert.Equal("p\"a\\ss", providers[0].Pass);
        Assert.True(providers[0].UseSsl);
        Assert.False(providers[1].UseSsl);
        Assert.Equal("news.a.com", providers[0].Host);
        Assert.Equal(563, providers[0].Port);
    }

    [Fact]
    public void UpThenDown_RestoresStreamingConnections()
    {
        // running Up then Down hands the carved connections back to the seeded providers.
        using var conn = OpenWithProviders(ProvidersJson);
        Exec(conn, SeedHealthCheckConnectionsFromStreaming.UpSql);
        Exec(conn, SeedHealthCheckConnectionsFromStreaming.DownSql);
        var providers = ReadProviders(conn).Providers;

        AssertProvider(providers[0], maxConnections: 15, healthCheckConnections: 0);
        AssertProvider(providers[1], maxConnections: 6, healthCheckConnections: 0);
        AssertProvider(providers[4], maxConnections: 30, healthCheckConnections: 9);
    }

    private static void AssertProvider(
        UsenetProviderConfig.ConnectionDetails provider, int maxConnections, int healthCheckConnections)
    {
        Assert.Equal(maxConnections, provider.MaxConnections);
        Assert.Equal(healthCheckConnections, provider.HealthCheckConnections);
    }

    private static UsenetProviderConfig RunSql(string providersJson, string sql)
    {
        using var conn = OpenWithProviders(providersJson);
        Exec(conn, sql);
        return ReadProviders(conn);
    }

    private static SqliteConnection OpenWithProviders(string providersJson)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        Exec(conn, "CREATE TABLE \"ConfigItems\" (\"ConfigName\" TEXT PRIMARY KEY, \"ConfigValue\" TEXT);");
        using var insert = conn.CreateCommand();
        insert.CommandText =
            "INSERT INTO \"ConfigItems\" (\"ConfigName\", \"ConfigValue\") VALUES ('usenet.providers', $v);";
        insert.Parameters.AddWithValue("$v", providersJson);
        insert.ExecuteNonQuery();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static UsenetProviderConfig ReadProviders(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE \"ConfigName\" = 'usenet.providers';";
        var json = (string)cmd.ExecuteScalar()!;
        return JsonSerializer.Deserialize<UsenetProviderConfig>(json)!;
    }
}
