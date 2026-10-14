using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D401CalculationResultTableInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TableInstanceId",
                schema: "calc",
                table: "CalculationStep",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TableInstanceId",
                schema: "calc",
                table: "CalculationResult",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TableInstanceId",
                schema: "calc",
                table: "CalculationInput",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TableInstanceId",
                schema: "calc",
                table: "CalculationStep");

            migrationBuilder.DropColumn(
                name: "TableInstanceId",
                schema: "calc",
                table: "CalculationResult");

            migrationBuilder.DropColumn(
                name: "TableInstanceId",
                schema: "calc",
                table: "CalculationInput");
        }
    }
}
