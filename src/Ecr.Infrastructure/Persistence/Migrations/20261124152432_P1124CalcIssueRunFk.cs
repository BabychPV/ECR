using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ P-1124: <c>calc.CalculationIssue</c> отримує <c>FK_CIssue_Run</c> (NO ACTION) на <c>calc.CalculationRun</c> та індекс
    /// <c>IX_CalculationIssue_Run</c>; перед ключем — одноразове прибирання сиріт (проблем, чийого прогону вже немає).
    /// </summary>
    /// <remarks>
    /// ⛔ Доти FK не було: прибирання D-560/D-575, нічна задача й видалення документа видаляли прогони, не чіпаючи їхніх
    /// проблем (323 сироти на копії live — таблиці старої версії шаблону після перебудови Land). Ключ без прибирання не
    /// створився б (547), а без ключа сироти росли б далі.
    /// <para>
    /// Крок 1 — не втратити живе: проблема таблиці, екземпляр якої в документі Є, а прогін не актуальний (прибраний або
    /// <c>Superseded</c>), — поточний стан неперерахованої таблиці; вона переходить на актуальний прогін свого документа ×
    /// періоду (документний має перевагу над проєктним, як у читанні результатів). Так само тепер робить перемикання
    /// (<c>CalculationIssueStore.CarryOverAsync</c>). Крок 2 — решта сиріт (прогону немає) видаляється. Обидва кроки
    /// ідемпотентні: повторний прогін нічого не знаходить.
    /// </para>
    /// </remarks>
    public partial class P1124CalcIssueRunFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE i
                   SET i.CalculationRunId = cur.Id
                  FROM calc.CalculationIssue AS i
                  JOIN doc.Document AS d ON d.Id = i.DocumentId
                 CROSS APPLY (SELECT TOP (1) r.Id
                                FROM calc.CalculationRun AS r
                               WHERE r.Status = N'Current'
                                 AND r.ProjectId = d.ProjectId
                                 AND r.PeriodKey = i.PeriodKey
                                 AND (r.DocumentId = i.DocumentId OR r.DocumentId IS NULL)
                               ORDER BY CASE WHEN r.DocumentId IS NULL THEN 1 ELSE 0 END, r.Id DESC) AS cur
                 WHERE i.CalculationRunId <> cur.Id
                   AND NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS own
                                    WHERE own.Id = i.CalculationRunId AND own.Status = N'Current')
                   AND EXISTS (SELECT 1 FROM doc.TableInstance AS t
                                WHERE t.DocumentId = i.DocumentId
                                  AND t.TableDefId = i.TableDefId
                                  AND t.PeriodKey = i.PeriodKey
                                  AND (t.SheetCopyId = i.SheetCopyId OR (t.SheetCopyId IS NULL AND i.SheetCopyId IS NULL)));

                DELETE i
                  FROM calc.CalculationIssue AS i
                 WHERE NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS r WHERE r.Id = i.CalculationRunId);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CalculationIssue_Run",
                schema: "calc",
                table: "CalculationIssue",
                column: "CalculationRunId");

            migrationBuilder.AddForeignKey(
                name: "FK_CIssue_Run",
                schema: "calc",
                table: "CalculationIssue",
                column: "CalculationRunId",
                principalSchema: "calc",
                principalTable: "CalculationRun",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        /// <remarks>Прибрані сироти не відновлюються (їхніх прогонів немає); знімаються лише ключ та індекс.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CIssue_Run",
                schema: "calc",
                table: "CalculationIssue");

            migrationBuilder.DropIndex(
                name: "IX_CalculationIssue_Run",
                schema: "calc",
                table: "CalculationIssue");
        }
    }
}
