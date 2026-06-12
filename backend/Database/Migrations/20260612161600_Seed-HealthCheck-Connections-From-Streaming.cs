using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <summary>
    /// Gives existing installs a working dedicated health-check pool without opening
    /// any extra connections: for each stats-capable provider (Pooled or
    /// BackupAndStats) that has no health-check connections yet and can spare them,
    /// move 3 connections out of MaxConnections (streaming) into HealthCheckConnections.
    /// The total connections opened to the provider is unchanged, so it can't exceed
    /// the account's connection cap. Providers that can't spare 3 (MaxConnections &lt; 6),
    /// backup-only providers, and providers already configured are left untouched
    /// (they keep the previous shared-pool behavior).
    /// </summary>
    public partial class SeedHealthCheckConnectionsFromStreaming : Migration
    {
        // The connections carved out per eligible provider == HealthCheckConnectionsPerCheck.
        // Kept as a literal here so the migration is self-contained.
        public const string UpSql = """
            UPDATE "ConfigItems"
            SET "ConfigValue" = (
                SELECT json_object('Providers', json_group_array(
                    json_object(
                        'Type', json_extract(p.value, '$.Type'),
                        'Host', json_extract(p.value, '$.Host'),
                        'Port', json_extract(p.value, '$.Port'),
                        'UseSsl', json(CASE WHEN json_extract(p.value, '$.UseSsl') IN (1, 'true') THEN 'true' ELSE 'false' END),
                        'User', json_extract(p.value, '$.User'),
                        'Pass', json_extract(p.value, '$.Pass'),
                        'MaxConnections', CASE
                            WHEN json_extract(p.value, '$.Type') IN (1, 2)
                             AND COALESCE(json_extract(p.value, '$.HealthCheckConnections'), 0) = 0
                             AND json_extract(p.value, '$.MaxConnections') >= 6
                            THEN json_extract(p.value, '$.MaxConnections') - 3
                            ELSE json_extract(p.value, '$.MaxConnections')
                        END,
                        'HealthCheckConnections', CASE
                            WHEN json_extract(p.value, '$.Type') IN (1, 2)
                             AND COALESCE(json_extract(p.value, '$.HealthCheckConnections'), 0) = 0
                             AND json_extract(p.value, '$.MaxConnections') >= 6
                            THEN 3
                            ELSE COALESCE(json_extract(p.value, '$.HealthCheckConnections'), 0)
                        END
                    )
                ))
                FROM json_each(
                    (SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = 'usenet.providers'),
                    '$.Providers'
                ) AS p
            )
            WHERE "ConfigName" = 'usenet.providers'
              AND json_valid("ConfigValue")
              AND json_type("ConfigValue", '$.Providers') = 'array';
            """;

        // Best-effort reverse: hand the 3 carved connections back to streaming for the
        // providers that look migration-seeded (stats-capable, exactly 3 health-check
        // connections). A user who manually set 3 would also be reverted — acceptable
        // for a rollback path.
        public const string DownSql = """
            UPDATE "ConfigItems"
            SET "ConfigValue" = (
                SELECT json_object('Providers', json_group_array(
                    json_object(
                        'Type', json_extract(p.value, '$.Type'),
                        'Host', json_extract(p.value, '$.Host'),
                        'Port', json_extract(p.value, '$.Port'),
                        'UseSsl', json(CASE WHEN json_extract(p.value, '$.UseSsl') IN (1, 'true') THEN 'true' ELSE 'false' END),
                        'User', json_extract(p.value, '$.User'),
                        'Pass', json_extract(p.value, '$.Pass'),
                        'MaxConnections', CASE
                            WHEN json_extract(p.value, '$.Type') IN (1, 2)
                             AND json_extract(p.value, '$.HealthCheckConnections') = 3
                            THEN json_extract(p.value, '$.MaxConnections') + 3
                            ELSE json_extract(p.value, '$.MaxConnections')
                        END,
                        'HealthCheckConnections', CASE
                            WHEN json_extract(p.value, '$.Type') IN (1, 2)
                             AND json_extract(p.value, '$.HealthCheckConnections') = 3
                            THEN 0
                            ELSE COALESCE(json_extract(p.value, '$.HealthCheckConnections'), 0)
                        END
                    )
                ))
                FROM json_each(
                    (SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = 'usenet.providers'),
                    '$.Providers'
                ) AS p
            )
            WHERE "ConfigName" = 'usenet.providers'
              AND json_valid("ConfigValue")
              AND json_type("ConfigValue", '$.Providers') = 'array';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql);
        }
    }
}
