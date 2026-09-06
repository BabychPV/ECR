using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class I7MaskedZeroTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "MaskedZero",
                schema: "calc",
                table: "CalculationStep",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateIndex(
                name: "IX_CStep_Masked",
                schema: "calc",
                table: "CalculationStep",
                columns: new[] { "PeriodKey", "MaskedZero" },
                filter: "[MaskedZero] <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CStep_Masked",
                schema: "calc",
                table: "CalculationStep");

            migrationBuilder.DropColumn(
                name: "MaskedZero",
                schema: "calc",
                table: "CalculationStep");
        }
    }
}
