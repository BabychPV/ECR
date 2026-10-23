using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>D-443</c> (BRIEF-1021 П.2, AN-130): учасники документа. Додає <c>doc.EditAssignment.Level</c>
    /// (<c>tinyint</c>, Read=1/Write=2/Submit=3/Approve=4) зі значенням за замовчуванням <c>2</c> — наявні
    /// передачі місяця стають учасниками рівня <c>Write</c> і поводяться як доти (підйом запису в зрізі,
    /// ексклюзивність, цикл «Заповнено/Валідовано»), плюс тепер дають видимість документа й аркушів зрізу.
    /// Область (документ/аркуш/таблиця) — наявні <c>SheetCode</c>/<c>TableCode</c>, окремої колонки немає.
    /// <c>CK_EditAssignment_LevelSlice</c>: місяць, період і ексклюзивність — лише для <c>Write</c>.
    /// Передача власника документа схеми не змінює: власник — <c>doc.Document.CreatedByUserId</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>Down</c> прибирає колонку, і учасники рівнів Read/Submit/Approve стали б передачами місяця
    /// (<c>Write</c>) з ключем зрізу <c>…|L=…</c> — тихе підвищення прав. Тому <c>Down</c> ПЕРШОЮ командою
    /// рахує такі рядки й зупиняється <c>THROW 50443</c> ДО зміни схеми (той самий механізм, що в
    /// <c>D440DropFourEyes</c>). Без <c>STRING_AGG</c>: підлога сервера — SQL Server 2016 (<c>D-101</c>).
    /// </remarks>
    public partial class D443DocumentParticipants : Migration
    {
        /// <summary>Номер помилки передперевірки відкату (<c>THROW</c>).</summary>
        public const int PrecheckErrorNumber = 50443;

        /// <summary>T-SQL передперевірки <c>Down</c>: <c>THROW 50443</c>, якщо є учасники рівня, відмінного від Write.</summary>
        public const string DownPrecheckSql = """
            DECLARE @d443Count int = (SELECT COUNT(*) FROM doc.EditAssignment WHERE [Level] <> 2);
            IF @d443Count > 0
            BEGIN
                DECLARE @d443List nvarchar(max) = ISNULL(STUFF((
                    SELECT TOP (10) N'; EditAssignment ' + CAST(a.Id AS nvarchar(20))
                           + N' (документ ' + CAST(a.DocumentId AS nvarchar(20))
                           + N', рівень ' + CAST(a.[Level] AS nvarchar(5)) + N')'
                    FROM doc.EditAssignment AS a
                    WHERE a.[Level] <> 2
                    ORDER BY a.Id
                    FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''), N'-');
                DECLARE @d443Message nvarchar(2048) = LEFT(
                      N'Відкат D443DocumentParticipants зупинено ДО зміни схеми: учасників документа рівнів '
                    + N'Read/Submit/Approve не можна перетворити на передачі місяця (Write) без підвищення прав. '
                    + N'Таких записів: ' + CAST(@d443Count AS nvarchar(20)) + N' (до десяти: ' + @d443List + N'). '
                    + N'Схему й дані не змінено. Зніміть цих учасників або відкотіться з бекапу і повторіть відкат.',
                    2048);
                THROW 50443, @d443Message, 1;
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Level",
                schema: "doc",
                table: "EditAssignment",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)2)
                .Annotation("Relational:DefaultConstraintName", "DF_EditAssignment_Level");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EditAssignment_Level",
                schema: "doc",
                table: "EditAssignment",
                sql: "[Level] BETWEEN 1 AND 4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EditAssignment_LevelSlice",
                schema: "doc",
                table: "EditAssignment",
                sql: "[Level] = 2 OR (MonthNumber IS NULL AND PeriodKey IS NULL AND IsExclusive = 0)");
        }

        /// <inheritdoc />
        /// <remarks>Учасники рівнів Read/Submit/Approve → <c>THROW 50443</c> з переліком (<see cref="DownPrecheckSql"/>).</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownPrecheckSql);

            migrationBuilder.DropCheckConstraint(
                name: "CK_EditAssignment_Level",
                schema: "doc",
                table: "EditAssignment");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EditAssignment_LevelSlice",
                schema: "doc",
                table: "EditAssignment");

            migrationBuilder.DropColumn(
                name: "Level",
                schema: "doc",
                table: "EditAssignment")
                .Annotation("Relational:DefaultConstraintName", "DF_EditAssignment_Level");
        }
    }
}
