using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// B66: <c>calc.CalculationIssue</c> отримує три NULL-колонки для нових видів проблем — <c>StepError</c> (крок методики
    /// впав: перший крок <c>StepCode</c>, код <c>ErrorCode</c>, кількість невдалих кроків рядка <c>AffectedCount</c>) і
    /// <c>RowInactive</c> (підсумок таблиці: кількість рядків, відсіяних фільтром активності D-425, у <c>AffectedCount</c>).
    /// </summary>
    /// <remarks>
    /// Лише додавання NULL-колонок: наявні рядки не змінюються, CHECK на <c>Kind</c> немає (вид — відкритий рядок), даних не
    /// переносимо. Ідемпотентність оновлення дає скрипт <c>migrations script --idempotent</c> (перевірка історії міграцій).
    /// </remarks>
    public partial class B66_CalculationIssueStepError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AffectedCount",
                schema: "calc",
                table: "CalculationIssue",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                schema: "calc",
                table: "CalculationIssue",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StepCode",
                schema: "calc",
                table: "CalculationIssue",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AffectedCount",
                schema: "calc",
                table: "CalculationIssue");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                schema: "calc",
                table: "CalculationIssue");

            migrationBuilder.DropColumn(
                name: "StepCode",
                schema: "calc",
                table: "CalculationIssue");
        }
    }
}
