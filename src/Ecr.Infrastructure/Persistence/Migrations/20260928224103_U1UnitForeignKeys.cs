using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// HSE301 U1 (аудит C6 п.1 і п.3): зовнішні ключі на <c>uom.Unit</c> з
    /// <c>cfg.ColumnDef.UnitId</c> і <c>cfg.RegistryFieldDef.UnitId</c>, індекс
    /// <c>calc.CalculationResult(UnitId)</c> під уже наявний <c>FK_CRes_Unit</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Передперевірка йде ПЕРШОЮ командою: база, де колонка чи поле посилається
    /// на одиницю, якої вже немає (до U1 видалення одиниці цих посилань не бачило
    /// ключем), зупиняється <c>THROW 50301</c> з переліком — таблиця, кількість,
    /// відсутні одиниці, перші рядки, — а не SQL-помилкою 547 без імен. Тихого
    /// обнулення немає: висяча одиниця — це колонка, значення якої вже рахувалися
    /// в якійсь одиниці, і вирішує, в якій, людина (runbook §8.2).
    ///
    /// ⚠ Механізм той самий, що в D148 (<c>D148ScalePrecheck</c>): <c>THROW</c>
    /// власного номера з переліком ДО зміни схеми, відсилка до runbook. На відміну
    /// від D148, міграція нова, тож перевірка лежить у її ж тілі, а не
    /// вставляється генератором (<c>EcrMigrationsSqlGenerator</c> потрібен лише
    /// для вже застосованих міграцій, які не переписуються). Обидва шляхи —
    /// <c>MigrateAsync</c> і <c>migration.sql --idempotent</c> під sqlcmd — виконують
    /// ту саму команду.
    ///
    /// ⚠ Індекс — сирим SQL, а не <c>CreateIndex</c>: <c>ONLINE = ON</c> є лише на
    /// Enterprise/Developer (<c>EngineEdition = 3</c>) і Azure (5, 8); на Standard
    /// і Express (2, 4) він падає помилкою 1712. Умова обчислюється на сервері
    /// (<see cref="IndexStatementSql"/>), тому обидва шляхи застосування дають
    /// однаковий результат. Місце — те саме, що в кластерного індексу таблиці:
    /// схема партиціонування разом із її стовпцем, або файлова група в профілі
    /// без партиціонування.
    /// </remarks>
    public partial class U1UnitForeignKeys : Migration
    {
        /// <summary>Номер помилки передперевірки: «висячі одиниці» (HSE301 U1).</summary>
        public const int PrecheckErrorNumber = 50301;

        /// <summary>Вираз редакції сервера, з яким міграція будує команду індексу.</summary>
        public const string EngineEditionExpression = "CAST(SERVERPROPERTY('EngineEdition') AS int)";

        /// <summary>Скільки рядків і одиниць показувати на таблицю.</summary>
        private const int SampleSize = 10;

        /// <summary>
        /// Таблиці, чиї посилання на одиницю отримують ключ: ім'я і стовпець
        /// власника рядка (для переліку).
        /// </summary>
        private static readonly (string Table, string Owner)[] Referencing =
        [
            ("cfg.ColumnDef", "TableDefId"),
            ("cfg.RegistryFieldDef", "RegistryDefId"),
        ];

        /// <summary>T-SQL передперевірки: <c>THROW 50301</c> з переліком, якщо є висячі посилання.</summary>
        /// <remarks>
        /// ⚠ <c>FOR XML PATH</c>, а не <c>STRING_AGG</c>: підлога сервера — 2016 SP1
        /// (<c>D-101</c>), а <c>STRING_AGG</c> з'явився у 2017.
        /// </remarks>
        public static string PrecheckSql { get; } = BuildPrecheckSql();

        /// <summary>
        /// Оголошення, що будують команду індексу в <c>@u1Create</c>, для заданої редакції.
        /// </summary>
        /// <param name="engineEdition">
        /// Вираз T-SQL типу <c>int</c>: <see cref="EngineEditionExpression"/> у міграції;
        /// літерал (<c>4</c>) у тесті, який перевіряє гілку без <c>ONLINE</c> на
        /// Developer-інстансі, де справжньої Express немає.
        /// </param>
        /// <returns>Текст, після якого <c>@u1Create</c> містить готову команду.</returns>
        public static string IndexStatementSql(string engineEdition)
            => $"""
                DECLARE @u1Edition int = {engineEdition};

                -- ⛔ ONLINE лише там, де він є: 3 — Enterprise/Developer/Evaluation,
                -- 5 — Azure SQL Database, 8 — Azure SQL Managed Instance. На Standard (2)
                -- і Express (4) ONLINE = ON — помилка 1712; там індекс будується офлайн,
                -- і таблиця на час побудови заблокована (runbook §8.2, вікно обслуговування).
                DECLARE @u1Online nvarchar(40) =
                    CASE WHEN @u1Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;

                -- Місце кластерного індексу таблиці: схема партиціонування з її
                -- стовпцем або файлова група. CREATE INDEX без ON на партиційованій
                -- таблиці теж вирівнявся б, але явне місце не залежить від цього
                -- правила і однакове з тим, що робить 07-partition-tables.sql.
                DECLARE @u1Place nvarchar(300) =
                (
                    SELECT QUOTENAME(ds.name)
                         + CASE WHEN ds.type = 'PS' THEN N'(' + QUOTENAME(c.name) + N')' ELSE N'' END
                    FROM sys.indexes AS i
                    JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
                    LEFT JOIN sys.index_columns AS ic
                      ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.partition_ordinal = 1
                    LEFT JOIN sys.columns AS c
                      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult')
                      AND i.index_id IN (0, 1)
                );

                DECLARE @u1Create nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX IX_CalculationResult_UnitId ON calc.CalculationResult (UnitId)'
                    + @u1Online
                    + ISNULL(N' ON ' + @u1Place, N'')
                    + N';';

                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrecheckSql);

            migrationBuilder.AddForeignKey(
                name: "FK_ColumnDef_Unit",
                schema: "cfg",
                table: "ColumnDef",
                column: "UnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RegField_Unit",
                schema: "cfg",
                table: "RegistryFieldDef",
                column: "UnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                IndexStatementSql(EngineEditionExpression)
                + """
                  IF INDEXPROPERTY(OBJECT_ID(N'calc.CalculationResult'), N'IX_CalculationResult_UnitId', 'IndexID') IS NULL
                      EXEC sys.sp_executesql @u1Create;
                  """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalculationResult_UnitId",
                schema: "calc",
                table: "CalculationResult");

            migrationBuilder.DropForeignKey(
                name: "FK_ColumnDef_Unit",
                schema: "cfg",
                table: "ColumnDef");

            migrationBuilder.DropForeignKey(
                name: "FK_RegField_Unit",
                schema: "cfg",
                table: "RegistryFieldDef");
        }

        private static string BuildPrecheckSql()
        {
            var sections = string.Concat(Referencing.Select(r => Section(r.Table, r.Owner)));

            return $"""
                DECLARE @u1Report nvarchar(max) = N'';
                DECLARE @u1Count int, @u1Rows nvarchar(max), @u1Units nvarchar(max);
                {sections}
                IF @u1Report <> N''
                BEGIN
                    DECLARE @u1Message nvarchar(2048) = LEFT(
                          N'Передперевірка U1: оновлення зупинено ДО зміни схеми. '
                        + N'Міграція U1UnitForeignKeys додає зовнішні ключі FK_ColumnDef_Unit і FK_RegField_Unit на uom.Unit, '
                        + N'а ці рядки посилаються на одиниці, яких у довіднику немає; SQL Server дав би 547 без переліку. '
                        + N'Висячі посилання:' + @u1Report + NCHAR(10)
                        + N'Схему й дані не змінено. Що робити: docs/admin/operations-runbook.md, п. 8.2.',
                        2048);
                    THROW {PrecheckErrorNumber}, @u1Message, 1;
                END
                """;
        }

        /// <summary>Підрахунок і перелік висячих посилань однієї таблиці в <c>@u1Report</c>.</summary>
        /// <param name="table">Таблиця зі стовпцем <c>UnitId</c>.</param>
        /// <param name="owner">Стовпець власника рядка — таблиця шаблону чи довідник.</param>
        private static string Section(string table, string owner)
        {
            var dangling = $"""
                FROM {table} AS r
                        WHERE r.UnitId IS NOT NULL
                          AND NOT EXISTS (SELECT 1 FROM uom.Unit AS u WHERE u.Id = r.UnitId)
                """;

            return $"""

                SELECT @u1Count = COUNT(*)
                {dangling};

                IF @u1Count > 0
                BEGIN
                    SET @u1Units = STUFF((
                        SELECT TOP ({SampleSize}) N', ' + CAST(d.UnitId AS nvarchar(20))
                        FROM (SELECT DISTINCT r.UnitId
                        {dangling}) AS d
                        ORDER BY d.UnitId
                        FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

                    SET @u1Rows = STUFF((
                        SELECT TOP ({SampleSize}) N', Id ' + CAST(r.Id AS nvarchar(20)) + N' ' + r.Code
                             + N' ({owner} ' + CAST(r.{owner} AS nvarchar(20))
                             + N', UnitId ' + CAST(r.UnitId AS nvarchar(20)) + N')'
                        {dangling}
                        ORDER BY r.Id
                        FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

                    SET @u1Report = @u1Report + NCHAR(10) + N'  {table}.UnitId: рядків '
                        + CAST(@u1Count AS nvarchar(20))
                        + N'; одиниць немає: ' + @u1Units
                        + N'; перші рядки: ' + @u1Rows;
                END

                """;
        }
    }
}
