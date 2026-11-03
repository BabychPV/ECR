using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// RS (копії аркушів, рішення людини 03.11): <c>cfg.SheetDef.IsCopyable</c>; копії в <c>doc.DocumentSheet</c>
    /// (<c>CopyOfId</c>, <c>CopyOrdinal</c>, <c>NameOverrideL10n</c>, <c>RowVersion</c>); <c>SheetCopyId</c> у
    /// <c>doc.TableInstance</c>, <c>wf.ApprovalState</c>, <c>wf.ApprovalEvent</c>, <c>calc.SubmissionSnapshot</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Без заповнення: наявні рядки — оригінали (<c>SheetCopyId</c> = <c>NULL</c>, <c>CopyOrdinal</c> = 1), тож
    /// поведінка не змінюється. <c>UQ_TableInstance</c> і <c>IX_TableInstance_SheetCopy</c> — сирим SQL: EF не вміє
    /// <c>ON ps_ByPeriodKey</c> (той самий прийом, що <see cref="BE21CellValueFillIndex"/> і
    /// <see cref="B18HotPathIndexes"/>). <c>UQ_TableInstance</c> перебудовується <c>DROP_EXISTING</c> (без вікна, коли
    /// індексу немає), ONLINE — лише там, де він є. Без схеми розділів (голе <c>dotnet ef database update</c>)
    /// індекс лягає на типову групу, і <c>07-partition-tables.sql</c> переносить його сам.
    /// </remarks>
    public partial class RS1_SheetCopies : Migration
    {
        /// <summary>Пакет, що будує <c>UQ_TableInstance</c> із заданими колонками на місці наявного.</summary>
        /// <param name="columns">Колонки ключа в квадратних дужках.</param>
        private static string RebuildTableInstanceUniqueSql(string columns) => $$"""
            DECLARE @rsOnline nvarchar(20) =
                CASE WHEN {{B18HotPathIndexes.EngineEditionExpression}} IN (3, 5, 8) THEN N', ONLINE = ON' ELSE N'' END;
            DECLARE @rsUq nvarchar(max) =
                N'CREATE UNIQUE INDEX [UQ_TableInstance] ON [doc].[TableInstance] ({{columns}}) '
                + N'WITH (DATA_COMPRESSION = NONE'
                + CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
                                    WHERE object_id = OBJECT_ID(N'doc.TableInstance') AND name = N'UQ_TableInstance')
                       THEN N', DROP_EXISTING = ON' ELSE N'' END
                + @rsOnline + N')'
                + CASE WHEN EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_ByPeriodKey')
                       THEN N' ON [ps_ByPeriodKey] ([PeriodKey])' ELSE N'' END
                + N';';
            PRINT @rsUq;
            EXEC sys.sp_executesql @rsUq;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SubmissionSnapshot_Sheet",
                schema: "calc",
                table: "SubmissionSnapshot");

            migrationBuilder.DropIndex(
                name: "UQ_DocumentSheet",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropIndex(
                name: "UQ_ApprovalState",
                schema: "wf",
                table: "ApprovalState");

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyId",
                schema: "doc",
                table: "TableInstance",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyId",
                schema: "calc",
                table: "SubmissionSnapshot",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCopyable",
                schema: "cfg",
                table: "SheetDef",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_SheetDef_Copy");

            migrationBuilder.AddColumn<long>(
                name: "CopyOfId",
                schema: "doc",
                table: "DocumentSheet",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "CopyOrdinal",
                schema: "doc",
                table: "DocumentSheet",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1)
                .Annotation("Relational:DefaultConstraintName", "DF_DocSheet_CopyOrd");

            migrationBuilder.AddColumn<string>(
                name: "NameOverrideL10n",
                schema: "doc",
                table: "DocumentSheet",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "doc",
                table: "DocumentSheet",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyId",
                schema: "wf",
                table: "ApprovalState",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyId",
                schema: "wf",
                table: "ApprovalEvent",
                type: "bigint",
                nullable: true);

            // ⛔ Без фільтра: NULL = оригінал, SQL Server вважає NULL рівними — рівно один оригінал.
            migrationBuilder.Sql(RebuildTableInstanceUniqueSql("[PeriodKey], [DocumentId], [TableDefId], [SheetCopyId]"));

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N'doc.TableInstance') AND name = N'IX_TableInstance_SheetCopy')
                BEGIN
                    DECLARE @rsCopyIx nvarchar(max) =
                        N'CREATE INDEX [IX_TableInstance_SheetCopy] ON [doc].[TableInstance] ([SheetCopyId]) '
                        + N'WHERE [SheetCopyId] IS NOT NULL WITH (DATA_COMPRESSION = NONE)'
                        + CASE WHEN EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_ByPeriodKey')
                               THEN N' ON [ps_ByPeriodKey] ([PeriodKey])' ELSE N'' END
                        + N';';
                    PRINT @rsCopyIx;
                    EXEC sys.sp_executesql @rsCopyIx;
                END
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionSnapshot_Sheet",
                schema: "calc",
                table: "SubmissionSnapshot",
                columns: new[] { "DocumentId", "SheetDefId", "PeriodKey", "SheetCopyId", "SubmittedAt" },
                descending: new[] { false, false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentSheet",
                schema: "doc",
                table: "DocumentSheet",
                columns: new[] { "DocumentId", "SheetDefId" },
                unique: true,
                filter: "[CopyOfId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentSheet_Copy",
                schema: "doc",
                table: "DocumentSheet",
                columns: new[] { "CopyOfId", "CopyOrdinal" },
                unique: true,
                filter: "[CopyOfId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocSheet_Copy",
                schema: "doc",
                table: "DocumentSheet",
                sql: "(CopyOfId IS NULL AND CopyOrdinal = 1) OR (CopyOfId IS NOT NULL AND CopyOrdinal >= 2)");

            migrationBuilder.CreateIndex(
                name: "UQ_ApprovalState",
                schema: "wf",
                table: "ApprovalState",
                columns: new[] { "DocumentId", "SheetDefId", "PeriodKey", "SheetCopyId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ApprState_SheetCopy",
                schema: "wf",
                table: "ApprovalState",
                column: "SheetCopyId",
                principalSchema: "doc",
                principalTable: "DocumentSheet",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocSheet_CopyOf",
                schema: "doc",
                table: "DocumentSheet",
                column: "CopyOfId",
                principalSchema: "doc",
                principalTable: "DocumentSheet",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TableInstance_SheetCopy",
                schema: "doc",
                table: "TableInstance",
                column: "SheetCopyId",
                principalSchema: "doc",
                principalTable: "DocumentSheet",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApprState_SheetCopy",
                schema: "wf",
                table: "ApprovalState");

            migrationBuilder.DropForeignKey(
                name: "FK_DocSheet_CopyOf",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropForeignKey(
                name: "FK_TableInstance_SheetCopy",
                schema: "doc",
                table: "TableInstance");

            migrationBuilder.DropIndex(
                name: "IX_TableInstance_SheetCopy",
                schema: "doc",
                table: "TableInstance");

            // ⚠ Відкат можливий лише без копій: інакше дублі (період, документ, таблиця) не пройдуть UNIQUE.
            migrationBuilder.Sql(RebuildTableInstanceUniqueSql("[PeriodKey], [DocumentId], [TableDefId]"));

            migrationBuilder.DropIndex(
                name: "IX_SubmissionSnapshot_Sheet",
                schema: "calc",
                table: "SubmissionSnapshot");

            migrationBuilder.DropIndex(
                name: "UQ_DocumentSheet",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropIndex(
                name: "UQ_DocumentSheet_Copy",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocSheet_Copy",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropIndex(
                name: "UQ_ApprovalState",
                schema: "wf",
                table: "ApprovalState");

            migrationBuilder.DropColumn(
                name: "SheetCopyId",
                schema: "doc",
                table: "TableInstance");

            migrationBuilder.DropColumn(
                name: "SheetCopyId",
                schema: "calc",
                table: "SubmissionSnapshot");

            migrationBuilder.DropColumn(
                name: "IsCopyable",
                schema: "cfg",
                table: "SheetDef")
                .Annotation("Relational:DefaultConstraintName", "DF_SheetDef_Copy");

            migrationBuilder.DropColumn(
                name: "CopyOfId",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropColumn(
                name: "CopyOrdinal",
                schema: "doc",
                table: "DocumentSheet")
                .Annotation("Relational:DefaultConstraintName", "DF_DocSheet_CopyOrd");

            migrationBuilder.DropColumn(
                name: "NameOverrideL10n",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropColumn(
                name: "SheetCopyId",
                schema: "wf",
                table: "ApprovalState");

            migrationBuilder.DropColumn(
                name: "SheetCopyId",
                schema: "wf",
                table: "ApprovalEvent");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionSnapshot_Sheet",
                schema: "calc",
                table: "SubmissionSnapshot",
                columns: new[] { "DocumentId", "SheetDefId", "PeriodKey", "SubmittedAt" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentSheet",
                schema: "doc",
                table: "DocumentSheet",
                columns: new[] { "DocumentId", "SheetDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_ApprovalState",
                schema: "wf",
                table: "ApprovalState",
                columns: new[] { "DocumentId", "SheetDefId", "PeriodKey" },
                unique: true);
        }
    }
}
