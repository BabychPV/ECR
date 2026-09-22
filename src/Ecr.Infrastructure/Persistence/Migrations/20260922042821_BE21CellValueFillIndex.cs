using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>Фільтрований індекс під підрахунок заповненості <c>tables/status</c>.</summary>
    /// <remarks>
    /// ⚠ <c>Sql</c>, а не <c>CreateIndex</c>: EF не вміє <c>ON ps_ByPeriodKey</c>, а
    /// індекс на <c>PRIMARY</c> на заповненій базі — ще одна повна перебудова в
    /// <c>07-partition-tables.sql</c>. Без схеми розділів індекс лягає на типову
    /// групу, і <c>07</c> переносить його сам. Фільтрований індекс потребує
    /// <c>QUOTED_IDENTIFIER ON</c>: його дають SqlClient і <c>sqlcmd -I</c> у <c>setup-dev-db.ps1</c>.
    /// </remarks>
    public partial class BE21CellValueFillIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N'doc.CellValue') AND name = N'IX_CellValue_Fill')
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_ByPeriodKey')
                        EXEC(N'CREATE INDEX [IX_CellValue_Fill] ON [doc].[CellValue] ([PeriodKey], [TableRowId], [ColumnDefId])
                               WHERE [IsCalculated] = 0 WITH (DATA_COMPRESSION = NONE) ON [ps_ByPeriodKey] ([PeriodKey]);');
                    ELSE
                        EXEC(N'CREATE INDEX [IX_CellValue_Fill] ON [doc].[CellValue] ([PeriodKey], [TableRowId], [ColumnDefId])
                               WHERE [IsCalculated] = 0 WITH (DATA_COMPRESSION = NONE);');
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CellValue_Fill",
                schema: "doc",
                table: "CellValue");
        }
    }
}
