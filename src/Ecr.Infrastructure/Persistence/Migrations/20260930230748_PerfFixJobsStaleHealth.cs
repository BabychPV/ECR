using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerfFixJobsStaleHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FanOutParentJobId",
                schema: "itg",
                table: "JobProgress",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                computedColumnSql: "CASE WHEN ISJSON([Payload]) = 1 THEN CAST(JSON_VALUE([Payload], N'$.fanOutParentJobId') AS nvarchar(100)) END",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_FanOutParent",
                schema: "itg",
                table: "JobProgress",
                column: "FanOutParentJobId")
                .Annotation("SqlServer:Include", new[] { "State" });

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_State_UpdatedAt",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "State", "UpdatedAt" })
                .Annotation("SqlServer:Include", new[] { "Message" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationRun_Project_FinishedAt",
                schema: "calc",
                table: "CalculationRun",
                columns: new[] { "ProjectId", "FinishedAt" })
                .Annotation("SqlServer:Include", new[] { "PeriodKey", "Status", "ErrorMessage" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobProgress_FanOutParent",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropIndex(
                name: "IX_JobProgress_State_UpdatedAt",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropIndex(
                name: "IX_CalculationRun_Project_FinishedAt",
                schema: "calc",
                table: "CalculationRun");

            migrationBuilder.DropColumn(
                name: "FanOutParentJobId",
                schema: "itg",
                table: "JobProgress");
        }
    }
}
