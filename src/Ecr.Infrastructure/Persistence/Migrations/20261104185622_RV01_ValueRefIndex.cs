using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RV01_ValueRefIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RegistryValue_ValueRef",
                schema: "dic",
                table: "RegistryValue",
                column: "ValueRefEntryId",
                filter: "[ValueRefEntryId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegistryValue_ValueRef",
                schema: "dic",
                table: "RegistryValue");
        }
    }
}
