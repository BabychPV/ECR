using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>Індекси журналу прогонів збору (ФВ-5.23): перелік за сутністю і покриття прогону.</summary>
    /// <remarks>Обидві таблиці <c>itg</c> не партиціоновані — звичайний <c>CreateIndex</c>.</remarks>
    public partial class FV523CollectionRunIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CollectionRun_SourceEntityId_Id",
                schema: "itg",
                table: "CollectionRun",
                columns: new[] { "SourceEntityId", "Id" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "StartedAt", "Status", "PointsRetrieved", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCoverage_CollectionRunId",
                schema: "itg",
                table: "CollectionCoverage",
                columns: new[] { "CollectionRunId", "CoveredFrom" })
                .Annotation("SqlServer:Include", new[] { "CoveredTo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CollectionRun_SourceEntityId_Id",
                schema: "itg",
                table: "CollectionRun");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCoverage_CollectionRunId",
                schema: "itg",
                table: "CollectionCoverage");
        }
    }
}
