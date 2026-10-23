using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HFIDX01_HeaderFieldDefLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HeaderFieldDef_LookupRegistryDefId",
                schema: "cfg",
                table: "HeaderFieldDef",
                column: "LookupRegistryDefId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HeaderFieldDef_LookupRegistryDefId",
                schema: "cfg",
                table: "HeaderFieldDef");
        }
    }
}
