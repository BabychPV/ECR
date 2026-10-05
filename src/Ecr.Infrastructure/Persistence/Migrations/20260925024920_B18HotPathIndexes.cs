using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// B-18 / R-05: індекси гарячих шляхів — посилання комірок на запис
    /// довідника й одиницю («Where used», перевірка видалення), живі рядки
    /// екземпляра таблиці, колонки шаблону за довідником і одиницею.
    /// </summary>
    /// <remarks>
    /// ⚠ Аудит L10-16: індекси будуються <c>ONLINE = ON</c> там, де він є
    /// (Enterprise/Developer — <c>EngineEdition = 3</c>, Azure — 5, 8), і офлайн на
    /// Standard і Express (2, 4), де <c>ONLINE</c> — помилка 1712. Той самий прийом,
    /// що в <see cref="U1UnitForeignKeys"/>: умова обчислюється на сервері, тож
    /// <c>MigrateAsync</c> і <c>migration.sql --idempotent</c> дають однаковий результат.
    /// На Standard таблиця на час побудови заблокована — вікно обслуговування
    /// (той самий порядок, що для U1, runbook §8.2). На вже оновлених базах міграція вдруге не виконується; правка
    /// діє для баз, що проходять її вперше.
    /// </remarks>
    public partial class B18HotPathIndexes : Migration
    {
        /// <summary>Вираз редакції сервера, з яким міграція будує команди індексів.</summary>
        public const string EngineEditionExpression = "CAST(SERVERPROPERTY('EngineEdition') AS int)";

        /// <summary>Індекси doc.*: таблиця, ім'я, форма (стовпці, INCLUDE, фільтр).</summary>
        private static readonly (string Table, string Index, string Shape)[] PartitionAligned =
        [
            ("TableRow", "IX_TableRow_Live",
                "([PeriodKey], [TableInstanceId]) INCLUDE ([RowKey]) WHERE [IsDeleted] = 0"),
            ("CellValue", "IX_CellValue_RegistryEntry",
                "([ValueRegistryEntryId]) WHERE [ValueRegistryEntryId] IS NOT NULL"),
            ("CellValue", "IX_CellValue_Unit",
                "([ValueUnitId]) WHERE [ValueUnitId] IS NOT NULL"),
        ];

        /// <summary>Індекси cfg.ColumnDef: ім'я й стовпець.</summary>
        private static readonly (string Index, string Column)[] ColumnDefIndexes =
        [
            ("IX_ColumnDef_LookupRegistryDefId", "LookupRegistryDefId"),
            ("IX_ColumnDef_UnitId", "UnitId"),
        ];

        /// <summary>
        /// Пакет T-SQL, що ставить усі п'ять індексів B18, яких ще немає.
        /// </summary>
        /// <param name="engineEdition">
        /// Вираз T-SQL типу <c>int</c>: <see cref="EngineEditionExpression"/> у міграції;
        /// літерал (<c>2</c>) у тесті, який перевіряє гілку без <c>ONLINE</c> на
        /// Developer-інстансі, де справжньої Standard немає.
        /// </param>
        /// <returns>
        /// Ідемпотентний текст: наявний індекс не перебудовується. Кожна виконана команда
        /// друкується (<c>PRINT</c>) — у журналі розгортання видно, ONLINE чи ні.
        /// </returns>
        public static string IndexesSql(string engineEdition)
        {
            var sql = new System.Text.StringBuilder($$"""
                DECLARE @b18Edition int = {{engineEdition}};

                -- ⛔ ONLINE лише там, де він є: 3 — Enterprise/Developer/Evaluation,
                -- 5 — Azure SQL Database, 8 — Managed Instance. На Standard (2) і
                -- Express (4) ONLINE = ON — помилка 1712; там побудова офлайн.
                DECLARE @b18Online nvarchar(20) =
                    CASE WHEN @b18Edition IN (3, 5, 8) THEN N', ONLINE = ON' ELSE N'' END;

                """);

            // doc.* — сирим SQL, як BE21CellValueFillIndex: EF не вміє
            // ON ps_ByPeriodKey, а індекс на PRIMARY на заповненій базі — ще одна
            // повна перебудова в 07-partition-tables.sql. Схеми розділів немає
            // (голе `dotnet ef database update` без 01/02) — індекс лягає на
            // типову групу, і 07 переносить його сам. Фільтровані індекси
            // потребують QUOTED_IDENTIFIER ON: його дають SqlClient і `sqlcmd -I`.
            foreach (var (table, index, shape) in PartitionAligned)
            {
                sql.Append($$"""
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE object_id = OBJECT_ID(N'doc.{{table}}') AND name = N'{{index}}')
                    BEGIN
                        DECLARE @b18_{{index}} nvarchar(max) =
                            N'CREATE INDEX [{{index}}] ON [doc].[{{table}}] {{shape}} '
                            + N'WITH (DATA_COMPRESSION = NONE' + @b18Online + N')'
                            + CASE WHEN EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_ByPeriodKey')
                                   THEN N' ON [ps_ByPeriodKey] ([PeriodKey])' ELSE N'' END
                            + N';';
                        PRINT @b18_{{index}};
                        EXEC sys.sp_executesql @b18_{{index}};
                    END

                    """);
            }

            foreach (var (index, column) in ColumnDefIndexes)
            {
                sql.Append($$"""
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE object_id = OBJECT_ID(N'cfg.ColumnDef') AND name = N'{{index}}')
                    BEGIN
                        DECLARE @b18_{{index}} nvarchar(max) =
                            N'CREATE INDEX [{{index}}] ON [cfg].[ColumnDef] ([{{column}}])'
                            + CASE WHEN @b18Online = N'' THEN N'' ELSE N' WITH (ONLINE = ON)' END
                            + N';';
                        PRINT @b18_{{index}};
                        EXEC sys.sp_executesql @b18_{{index}};
                    END

                    """);
            }

            return sql.ToString();
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql(IndexesSql(EngineEditionExpression));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TableRow_Live",
                schema: "doc",
                table: "TableRow");

            migrationBuilder.DropIndex(
                name: "IX_ColumnDef_LookupRegistryDefId",
                schema: "cfg",
                table: "ColumnDef");

            migrationBuilder.DropIndex(
                name: "IX_ColumnDef_UnitId",
                schema: "cfg",
                table: "ColumnDef");

            migrationBuilder.DropIndex(
                name: "IX_CellValue_RegistryEntry",
                schema: "doc",
                table: "CellValue");

            migrationBuilder.DropIndex(
                name: "IX_CellValue_Unit",
                schema: "doc",
                table: "CellValue");
        }
    }
}
