using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Analiz1JobsCoverageIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstanceId",
                schema: "itg",
                table: "JobProgress",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_CreatedBy_UpdatedAt",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "CreatedByUserId", "UpdatedAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "State", "JobCode" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCoverage_SourceEntity_CoveredTo",
                schema: "itg",
                table: "CollectionCoverage",
                columns: new[] { "SourceEntityId", "CoveredTo" },
                filter: "[Status] IS NULL")
                .Annotation("SqlServer:Include", new[] { "CoveredFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobProgress_CreatedBy_UpdatedAt",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCoverage_SourceEntity_CoveredTo",
                schema: "itg",
                table: "CollectionCoverage");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                schema: "itg",
                table: "JobProgress");
        }
    }
}
