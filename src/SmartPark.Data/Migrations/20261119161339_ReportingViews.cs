using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartPark.Data.Migrations
{
    /// <summary>
    /// SQL-представлення для звітів (схема reporting) – спрощена заміна
    /// аналітичного сховища ClickHouse цільової архітектури.
    /// </summary>
    public partial class ReportingViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS reporting;");

            // Дохід, кількість сесій і середня тривалість за днями (за київським часом).
            migrationBuilder.Sql("""
                CREATE VIEW reporting.daily_revenue AS
                SELECT (s."ExitTime" AT TIME ZONE 'Europe/Kyiv')::date AS day,
                       p."Name"                                       AS parking,
                       count(*)::int                                  AS sessions,
                       coalesce(sum(s."Amount"), 0)::numeric(12, 2)   AS revenue,
                       coalesce(avg(extract(epoch FROM s."ExitTime" - s."EntryTime") / 60), 0)::float8 AS avg_minutes
                FROM session."ParkingSessions" s
                JOIN catalog."Parkings" p ON p."Id" = s."ParkingId"
                WHERE s."ExitTime" IS NOT NULL
                GROUP BY 1, 2;
                """);

            // Поточна зайнятість активних паркінгів.
            migrationBuilder.Sql("""
                CREATE VIEW reporting.current_occupancy AS
                SELECT p."Name"                                                  AS parking,
                       count(sp."Id")::int                                       AS total,
                       count(sp."Id") FILTER (WHERE sp."Status" = 'Occupied')::int AS occupied,
                       count(sp."Id") FILTER (WHERE sp."Status" = 'Free')::int     AS free,
                       coalesce(round(100.0 * count(sp."Id") FILTER (WHERE sp."Status" = 'Occupied')
                                      / nullif(count(sp."Id"), 0), 1), 0)::float8  AS occupancy_percent
                FROM catalog."Parkings" p
                JOIN catalog."Spots" sp ON sp."ParkingId" = p."Id"
                WHERE p."Status" = 'Active'
                GROUP BY p."Name";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS reporting.current_occupancy;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS reporting.daily_revenue;");
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS reporting;");
        }
    }
}
