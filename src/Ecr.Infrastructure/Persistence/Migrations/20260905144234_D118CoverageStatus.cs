using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D118CoverageStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Details",
                schema: "itg",
                table: "CollectionCoverage",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeriodKey",
                schema: "itg",
                table: "CollectionCoverage",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "itg",
                table: "CollectionCoverage",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Details",
                schema: "itg",
                table: "CollectionCoverage");

            migrationBuilder.DropColumn(
                name: "PeriodKey",
                schema: "itg",
                table: "CollectionCoverage");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "itg",
                table: "CollectionCoverage");
        }
    }
}
