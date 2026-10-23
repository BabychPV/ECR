using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>D-440</c> (BRIEF-1021 П.4, AN-127): правило чотирьох очей скасоване безумовно. Знімає
    /// <c>CK_MV_FourEyes</c> (автор версії методології ≠ той, хто публікує) і <c>CK_RecalcApproval_FourEyes</c>
    /// (ініціатор перерахунку закритого періоду ≠ той, хто підтверджує). Публікацію й підтвердження
    /// розподіляють лише ролі й гранти.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>Down</c> повертає обидва CHECK, але після <c>Up</c> у базі законно з'являються рядки, які їх
    /// порушують (автор сам опублікував версію; ініціатор сам підтвердив перерахунок). <c>ADD CONSTRAINT</c>
    /// на таких даних падає голою 547 без жодного рядка. Тому <c>Down</c> ПЕРШОЮ командою рахує порушників і
    /// зупиняється <c>THROW 50440</c> з переліком ДО зміни схеми — той самий механізм, що в
    /// <c>D256SmtpSettingsAndChannelRoles</c> і <c>AN80CarryOverInputsAsOfCategoryRuleLength</c>. Дані не
    /// змінюються мовчки: перевидати чи прибрати такі записи — рішення адміністратора (або відкат із бекапу).
    /// Без <c>STRING_AGG</c>: підлога сервера — SQL Server 2016 (<c>D-101</c>).
    /// </remarks>
    public partial class D440DropFourEyes : Migration
    {
        /// <summary>Номер помилки передперевірки відкату (<c>THROW</c>).</summary>
        public const int PrecheckErrorNumber = 50440;

        /// <summary>
        /// T-SQL передперевірки <c>Down</c>: <c>THROW 50440</c> з переліком записів, що порушили б
        /// <c>CK_MV_FourEyes</c> або <c>CK_RecalcApproval_FourEyes</c>.
        /// </summary>
        public const string DownPrecheckSql = """
            DECLARE @d440Mv int = (SELECT COUNT(*) FROM calc.MethodologyVersion
                                   WHERE PublishedByUserId IS NOT NULL AND PublishedByUserId = CreatedByUserId);
            DECLARE @d440Ra int = (SELECT COUNT(*) FROM calc.RecalculationApproval
                                   WHERE ConfirmedByUserId IS NOT NULL AND ConfirmedByUserId = RequestedByUserId);
            IF @d440Mv > 0 OR @d440Ra > 0
            BEGIN
                DECLARE @d440MvList nvarchar(max) = ISNULL(STUFF((
                    SELECT TOP (10) N'; MethodologyVersion ' + CAST(v.Id AS nvarchar(20))
                           + N' (версія ' + v.[Version] + N', користувач ' + CAST(v.CreatedByUserId AS nvarchar(20)) + N')'
                    FROM calc.MethodologyVersion AS v
                    WHERE v.PublishedByUserId IS NOT NULL AND v.PublishedByUserId = v.CreatedByUserId
                    ORDER BY v.Id
                    FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''), N'-');
                DECLARE @d440RaList nvarchar(max) = ISNULL(STUFF((
                    SELECT TOP (10) N'; RecalculationApproval ' + CAST(a.Id AS nvarchar(20))
                           + N' (період ' + CAST(a.PeriodKey AS nvarchar(20))
                           + N', користувач ' + CAST(a.RequestedByUserId AS nvarchar(20)) + N')'
                    FROM calc.RecalculationApproval AS a
                    WHERE a.ConfirmedByUserId IS NOT NULL AND a.ConfirmedByUserId = a.RequestedByUserId
                    ORDER BY a.Id
                    FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''), N'-');
                DECLARE @d440Message nvarchar(2048) = LEFT(
                      N'Відкат D440DropFourEyes зупинено ДО зміни схеми: правило чотирьох очей (CK_MV_FourEyes, '
                    + N'CK_RecalcApproval_FourEyes) не повернути, поки в базі є записи, погоджені власним автором. '
                    + N'Версій методології, опублікованих автором: ' + CAST(@d440Mv AS nvarchar(20))
                    + N' (до десяти: ' + @d440MvList + N'). '
                    + N'Погоджень перерахунку, підтверджених ініціатором: ' + CAST(@d440Ra AS nvarchar(20))
                    + N' (до десяти: ' + @d440RaList + N'). '
                    + N'Схему й дані не змінено. Відкотіться з бекапу або вирішіть долю цих записів і повторіть відкат.',
                    2048);
                THROW 50440, @d440Message, 1;
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RecalcApproval_FourEyes",
                schema: "calc",
                table: "RecalculationApproval");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MV_FourEyes",
                schema: "calc",
                table: "MethodologyVersion");
        }

        /// <inheritdoc />
        /// <remarks>Записи, погоджені власним автором, → <c>THROW 50440</c> з переліком (<see cref="DownPrecheckSql"/>).</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownPrecheckSql);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecalcApproval_FourEyes",
                schema: "calc",
                table: "RecalculationApproval",
                sql: "ConfirmedByUserId IS NULL OR ConfirmedByUserId <> RequestedByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MV_FourEyes",
                schema: "calc",
                table: "MethodologyVersion",
                sql: "PublishedByUserId IS NULL OR PublishedByUserId <> CreatedByUserId");
        }
    }
}
