using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ D-565 (PERF-03): прапор методології «трасувати проміжні кроки» (<c>calc.Methodology.TraceSteps</c>,
    /// DEFAULT 0 — кроки не пишуться) і стиснений трейс кроку (<c>calc.CalculationStep.TraceJsonGz</c>).
    /// </summary>
    /// <remarks>
    /// Обидві зміни — лише метадані: нова NULL-колонка на партиціонованій <c>calc.CalculationStep</c> не переписує
    /// рядків, а <c>calc.Methodology</c> — десятки рядків. Наявні кроки лишаються в <c>TraceJson nvarchar(max)</c> і
    /// читаються як і раніше (мішаний формат, перенесення даних немає — старі кроки прибирає D-560).
    /// </remarks>
    public partial class PERF03TraceStepsFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TraceSteps",
                schema: "calc",
                table: "Methodology",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_Methodology_TraceSteps");

            migrationBuilder.AddColumn<byte[]>(
                name: "TraceJsonGz",
                schema: "calc",
                table: "CalculationStep",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceSteps",
                schema: "calc",
                table: "Methodology")
                .Annotation("Relational:DefaultConstraintName", "DF_Methodology_TraceSteps");

            migrationBuilder.DropColumn(
                name: "TraceJsonGz",
                schema: "calc",
                table: "CalculationStep");
        }
    }
}
