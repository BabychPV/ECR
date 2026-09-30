using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Документний вимір «актуальності» прогону (третя хвиля UX-PASS R4,
    /// «CalculationRun ховає результати сусідніх документів»).
    /// </summary>
    /// <remarks>
    /// ⛔ Доти <c>calc.CalculationRun</c> знав лише проєкт і період: прогін
    /// ОДНОГО документа (<c>RecalculateDocumentHandler</c>) і прогін УСЬОГО
    /// проєкту того самого <c>(ProjectId, PeriodKey)</c> змагалися за той
    /// самий унікальний слот <c>UX_CalculationRun_Current</c>. Перерахунок
    /// документа A завершувався, <c>SwitchCurrentRunAsync</c> знімав
    /// актуальність з УСІХ інших прогонів області — зокрема з прогону
    /// документа B, хоча в документі B нічого не змінювалося. Результати
    /// документа B зникали з екрана й експорту без жодної помилки.
    ///
    /// ⚠ <c>DocumentId IS NULL</c> — прогін усього проєкту й періоду, як і
    /// раніше (SQL Server трактує NULL як рівний NULL в унікальному індексі,
    /// той самий прийом, що вже дає окрему область річному прогону через
    /// <c>PeriodKey IS NULL</c>). Дані наявних прогонів не чіпаються: старий
    /// стовпець просто отримує NULL, і жодна наявна актуальність не змінює
    /// значення — рівно та поведінка, що діяла до цієї міграції.
    /// </remarks>
    public partial class CalculationRunDocumentScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CalculationRun_Current",
                schema: "calc",
                table: "CalculationRun");

            migrationBuilder.AddColumn<long>(
                name: "DocumentId",
                schema: "calc",
                table: "CalculationRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_CalculationRun_Current",
                schema: "calc",
                table: "CalculationRun",
                columns: new[] { "ProjectId", "PeriodKey", "DocumentId" },
                unique: true,
                filter: "[Status] = 'Current'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CalculationRun_Current",
                schema: "calc",
                table: "CalculationRun");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                schema: "calc",
                table: "CalculationRun");

            migrationBuilder.CreateIndex(
                name: "UX_CalculationRun_Current",
                schema: "calc",
                table: "CalculationRun",
                columns: new[] { "ProjectId", "PeriodKey" },
                unique: true,
                filter: "[Status] = 'Current'");
        }
    }
}
