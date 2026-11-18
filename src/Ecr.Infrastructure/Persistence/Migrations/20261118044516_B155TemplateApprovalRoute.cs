using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// B155: <c>wf.ApprovalRoute.TemplateId</c> і унікальний <c>UQ_AR_Scope (ProjectId, TemplateId) WHERE
    /// TemplateVersionId IS NULL</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ B173: до B155 у всіх рядків <c>TemplateId = NULL</c>, а унікальний індекс бачить NULL рівними, тож база з
    /// двома маршрутами без версії на один проєкт (або двома типовими, <c>ProjectId NULL</c>) дала б сире
    /// 1505/2601 без переліку. Передперевірка зупиняє <c>THROW 50155</c> з переліком ДО зміни схеми.
    /// Маршрути не видаляються й не зливаються: це видалення даних, його робить людина (runbook, п. 8.10).
    /// Ту саму команду виконують <c>MigrateAsync</c> і ідемпотентний <c>migration.sql</c>.
    /// </remarks>
    public partial class B155TemplateApprovalRoute : Migration
    {
        /// <summary>Номер помилки передперевірки: «дублі маршрутів погодження» (B173).</summary>
        public const int PreflightErrorNumber = 50155;

        /// <summary>Скільки груп (проєктів) показувати в повідомленні.</summary>
        private const int SampleSize = 10;

        /// <summary>T-SQL передперевірки: <c>THROW 50155</c> з переліком, якщо на один проєкт більше одного маршруту без версії.</summary>
        /// <remarks>⚠ <c>FOR XML PATH</c>, а не <c>STRING_AGG</c>: підлога сервера - 2016 SP1 (<c>D-101</c>).</remarks>
        public static string PreflightSql { get; } = $"""
            DECLARE @b155Groups int =
                (SELECT COUNT(*) FROM (SELECT r.ProjectId FROM wf.ApprovalRoute AS r
                    WHERE r.TemplateVersionId IS NULL GROUP BY r.ProjectId HAVING COUNT(*) > 1) AS g);

            IF @b155Groups > 0
            BEGIN
                DECLARE @b155List nvarchar(max) = (
                    SELECT TOP ({SampleSize})
                           NCHAR(10) + N'  '
                         + CASE WHEN g.ProjectId IS NULL THEN N'типовий маршрут майданчика (ProjectId NULL)'
                                ELSE N'проєкт ' + CAST(g.ProjectId AS nvarchar(20)) END
                         + N': ' + STUFF((
                               SELECT N', ' + CAST(r.Id AS nvarchar(20)) + N' ' + r.Code
                                    + CASE WHEN r.IsActive = 1 THEN N' (активний)' ELSE N' (вимкнений)' END
                               FROM wf.ApprovalRoute AS r
                               WHERE r.TemplateVersionId IS NULL
                                 AND (r.ProjectId = g.ProjectId OR (r.ProjectId IS NULL AND g.ProjectId IS NULL))
                               ORDER BY r.Code COLLATE Latin1_General_BIN2
                               FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'')
                    FROM (SELECT r.ProjectId FROM wf.ApprovalRoute AS r
                          WHERE r.TemplateVersionId IS NULL GROUP BY r.ProjectId HAVING COUNT(*) > 1) AS g
                    ORDER BY g.ProjectId
                    FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)');

                DECLARE @b155Message nvarchar(2048) =
                      N'Передперевірка B173: оновлення зупинено ДО зміни схеми. '
                    + N'Міграція B155TemplateApprovalRoute додає унікальний індекс UQ_AR_Scope (проєкт, шаблон) на маршрути без версії, '
                    + N'а на цих проєктах їх кілька; SQL Server дав би 1505 без переліку. '
                    + N'Груп: ' + CAST(@b155Groups AS nvarchar(20)) + N'; маршрути (Id, код):'
                    + CASE WHEN LEN(@b155List) > 1300 THEN LEFT(@b155List, 1300) + N'…' ELSE @b155List END + NCHAR(10)
                    + N'Схему й дані не змінено, маршрути не видалено. Лишіть по одному маршруту без версії на проєкт '
                    + N'(застосунок зараз бере активний із найменшим кодом) і повторіть. Що робити: docs/admin/operations-runbook.md, п. 8.10.';
                THROW {PreflightErrorNumber}, @b155Message, 1;
            END
            """;
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PreflightSql);

            migrationBuilder.DropIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.AddColumn<int>(
                name: "TemplateId",
                schema: "wf",
                table: "ApprovalRoute",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute",
                columns: new[] { "ProjectId", "TemplateId", "TemplateVersionId" });

            migrationBuilder.CreateIndex(
                name: "UQ_AR_Scope",
                schema: "wf",
                table: "ApprovalRoute",
                columns: new[] { "ProjectId", "TemplateId" },
                unique: true,
                filter: "[TemplateVersionId] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AR_Template",
                schema: "wf",
                table: "ApprovalRoute",
                column: "TemplateId",
                principalSchema: "cfg",
                principalTable: "Template",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AR_Template",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropIndex(
                name: "UQ_AR_Scope",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute",
                columns: new[] { "ProjectId", "TemplateVersionId" });
        }
    }
}
