using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class UpdateMissingReportDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AlertRules" AS rules
                SET "ParametersJson" = json_build_object('days', settings."MissingReportThresholdDays"::text)::text
                FROM "NotificationSettings" AS settings
                WHERE settings."Id" = 1 AND rules."DefaultKey" = 'MissedReport' AND rules."ParametersJson" IS NULL;

                UPDATE "NotificationSettings" SET "MissingReportThresholdDays" = 3
                WHERE "Id" = 1 AND "MissingReportThresholdDays" = 2;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "NotificationSettings" SET "MissingReportThresholdDays" = 2
                WHERE "Id" = 1 AND "MissingReportThresholdDays" = 3;
                """);
        }
    }
}
