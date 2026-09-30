using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// B-18 / R-05: індекси гарячих шляхів — посилання комірок на запис
    /// довідника й одиницю («Where used», перевірка видалення), живі рядки
    /// екземпляра таблиці, колонки шаблону за довідником і одиницею.
    /// </summary>
    public partial class B18HotPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // doc.* — через Sql, як BE21CellValueFillIndex: EF не вміє
            // ON ps_ByPeriodKey, а індекс на PRIMARY на заповненій базі — ще одна
            // повна перебудова в 07-partition-tables.sql. Схеми розділів немає
            // (голе `dotnet ef database update` без 01/02) — індекс лягає на
            // типову групу, і 07 переносить його сам. Фільтровані індекси
            // потребують QUOTED_IDENTIFIER ON: його дають SqlClient і `sqlcmd -I`.
            CreatePartitionAligned(
                migrationBuilder, "TableRow", "IX_TableRow_Live",
                "([PeriodKey], [TableInstanceId]) INCLUDE ([RowKey]) WHERE [IsDeleted] = 0");

            CreatePartitionAligned(
                migrationBuilder, "CellValue", "IX_CellValue_RegistryEntry",
                "([ValueRegistryEntryId]) WHERE [ValueRegistryEntryId] IS NOT NULL");

            CreatePartitionAligned(
                migrationBuilder, "CellValue", "IX_CellValue_Unit",
                "([ValueUnitId]) WHERE [ValueUnitId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ColumnDef_LookupRegistryDefId",
                schema: "cfg",
                table: "ColumnDef",
                column: "LookupRegistryDefId");

            migrationBuilder.CreateIndex(
                name: "IX_ColumnDef_UnitId",
                schema: "cfg",
                table: "ColumnDef",
                column: "UnitId");
        }

        /// <summary>Некластерний індекс doc.*, вирівняний по ps_ByPeriodKey, якщо схема є.</summary>
        private static void CreatePartitionAligned(
            MigrationBuilder migrationBuilder, string table, string index, string shape)
        {
            migrationBuilder.Sql($$"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N'doc.{{table}}') AND name = N'{{index}}')
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_ByPeriodKey')
                        EXEC(N'CREATE INDEX [{{index}}] ON [doc].[{{table}}] {{shape}}
                               WITH (DATA_COMPRESSION = NONE) ON [ps_ByPeriodKey] ([PeriodKey]);');
                    ELSE
                        EXEC(N'CREATE INDEX [{{index}}] ON [doc].[{{table}}] {{shape}}
                               WITH (DATA_COMPRESSION = NONE);');
                END
                """);
        }

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
