using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D463RawDataReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EntityFieldMap_TargetCell",
                schema: "ext",
                table: "EntityFieldMap",
                columns: new[] { "TargetColumnDefId", "TargetRowKey" },
                filter: "[TargetColumnDefId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCoverage_PeriodEvents",
                schema: "itg",
                table: "CollectionCoverage",
                columns: new[] { "SourceEntityId", "PeriodKey", "Id" },
                filter: "[Status] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "Status", "Details", "CoveredFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EntityFieldMap_TargetCell",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCoverage_PeriodEvents",
                schema: "itg",
                table: "CollectionCoverage");
        }
    }
}
