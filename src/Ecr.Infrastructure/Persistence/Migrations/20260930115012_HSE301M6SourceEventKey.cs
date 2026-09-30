using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HSE301M6SourceEventKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrimaryElement",
                schema: "ext",
                table: "SourceEventLink",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_SEL_NaturalKey",
                schema: "ext",
                table: "SourceEventLink",
                columns: new[] { "SourceEventMapId", "StartUtc", "PrimaryElement" },
                unique: true,
                filter: "[PrimaryElement] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SEL_NaturalKey",
                schema: "ext",
                table: "SourceEventLink");

            migrationBuilder.DropColumn(
                name: "PrimaryElement",
                schema: "ext",
                table: "SourceEventLink");
        }
    }
}
