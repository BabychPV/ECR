using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class A1RegistryLookupFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_CellValue_Entry",
                schema: "doc",
                table: "CellValue",
                column: "ValueRegistryEntryId",
                principalSchema: "dic",
                principalTable: "RegistryEntry",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CellValue_Entry",
                schema: "doc",
                table: "CellValue");
        }
    }
}
