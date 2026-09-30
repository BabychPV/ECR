using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HSE301M3Trace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPerSubstance",
                schema: "calc",
                table: "MethodologyOutput",
                type: "bit",
                nullable: false,
                defaultValue: true)
                .Annotation("Relational:DefaultConstraintName", "DF_MO_PerSub");

            migrationBuilder.AddColumn<bool>(
                name: "IsVisible",
                schema: "calc",
                table: "MethodologyFormula",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_MF_Visible");

            migrationBuilder.AddColumn<byte>(
                name: "Scope",
                schema: "calc",
                table: "MethodologyFormula",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0)
                .Annotation("Relational:DefaultConstraintName", "DF_MF_Scope");

            migrationBuilder.AddColumn<long>(
                name: "DocumentId",
                schema: "calc",
                table: "CalculationStep",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceRowKey",
                schema: "calc",
                table: "CalculationStep",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubstanceEntryId",
                schema: "calc",
                table: "CalculationStep",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                schema: "calc",
                table: "CalculationResult",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0)
                .Annotation("Relational:DefaultConstraintName", "DF_CRes_Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPerSubstance",
                schema: "calc",
                table: "MethodologyOutput")
                .Annotation("Relational:DefaultConstraintName", "DF_MO_PerSub");

            migrationBuilder.DropColumn(
                name: "IsVisible",
                schema: "calc",
                table: "MethodologyFormula")
                .Annotation("Relational:DefaultConstraintName", "DF_MF_Visible");

            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "calc",
                table: "MethodologyFormula")
                .Annotation("Relational:DefaultConstraintName", "DF_MF_Scope");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                schema: "calc",
                table: "CalculationStep");

            migrationBuilder.DropColumn(
                name: "SourceRowKey",
                schema: "calc",
                table: "CalculationStep");

            migrationBuilder.DropColumn(
                name: "SubstanceEntryId",
                schema: "calc",
                table: "CalculationStep");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "calc",
                table: "CalculationResult")
                .Annotation("Relational:DefaultConstraintName", "DF_CRes_Kind");
        }
    }
}
