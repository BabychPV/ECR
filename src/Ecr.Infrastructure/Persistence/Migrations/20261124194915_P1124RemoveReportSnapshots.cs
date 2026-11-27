using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ P-1124-3 (D-NEW; людина 24.11: «Можеш прибрати якщо вони не потрібні, зауваж що дані розрахунків, методологія тощо
    /// мають бути доступні для SSRS»): прибирає конструктор звітів і зрізи звітності з бази — вʼюху зрізу
    /// <c>rpt.v_WaterReport_v1</c>, таблиці <c>rpt.ReportRow</c>/<c>ReportSnapshot</c>/<c>ReportVersion</c>/<c>ReportDef</c> і
    /// права <c>Report.BuildSnapshot</c>/<c>ViewSnapshot</c>/<c>Export</c>/<c>EditDefinition</c> разом із їхніми роздачами.
    /// </summary>
    /// <remarks>
    /// Доказ «не потрібні» (24.11, live EcrLandUi лише читанням): зрізів 0, рядків 0, описів 4 і версій 4 (лише сід);
    /// <c>sys.sql_expression_dependencies</c> — на таблиці посилається тільки <c>rpt.v_WaterReport_v1</c>, на неї — ніщо;
    /// зовнішні ключі — лише між цими чотирма таблицями; бази <c>ReportServer</c> на інстансі немає; у репозиторії RDL немає,
    /// а 166 RDL замовника в <c>docs/</c> не згадують <c>rpt.*</c>. Роль <c>ReportViewer</c> ЛИШАЄТЬСЯ: на live її мають
    /// 2 користувачі (з правом <c>Report.ViewRegulatory</c>). Сирий шар SSRS (<c>rpt.v_DocumentCells</c>, широкі вʼюхи, вʼюхи
    /// результатів і методологій у <c>05-rpt-views.sql</c>) не зачеплено.
    /// <para>
    /// Порядок: вʼюха → роздачі → права → таблиці (від залежних до головних). Ідемпотентний скрипт міграцій удруге змін не дає
    /// (міграція записана в історії). <c>Down</c> відтворює порожні таблиці (дані зрізів не відновлюються; вʼюху й права
    /// повертає лише відкат коду разом із сідом).
    /// </para>
    /// </remarks>
    public partial class P1124RemoveReportSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS rpt.v_WaterReport_v1;");

            migrationBuilder.Sql("""
                DELETE FROM sec.RolePermission
                 WHERE PermissionCode IN (N'Report.BuildSnapshot', N'Report.ViewSnapshot', N'Report.Export', N'Report.EditDefinition');
                DELETE FROM sec.Permission
                 WHERE Code IN (N'Report.BuildSnapshot', N'Report.ViewSnapshot', N'Report.Export', N'Report.EditDefinition');
                """);

            migrationBuilder.DropTable(
                name: "ReportRow",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "ReportSnapshot",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "ReportVersion",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "ReportDef",
                schema: "rpt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rpt");

            migrationBuilder.CreateTable(
                name: "ReportDef",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsRegulatory = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDef", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportVersion",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ColumnsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReportDefId = table.Column<int>(type: "int", nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RV_Def",
                        column: x => x.ReportDefId,
                        principalSchema: "rpt",
                        principalTable: "ReportDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportSnapshot",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BuiltAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    BuiltByUserId = table.Column<int>(type: "int", nullable: true),
                    CalculationRunId = table.Column<long>(type: "bigint", nullable: true),
                    ContentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    HashFormat = table.Column<string>(type: "varchar(8)", nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PeriodKey = table.Column<int>(type: "int", nullable: true),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ReportVersionId = table.Column<int>(type: "int", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Status = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSnapshot", x => x.Id);
                    table.CheckConstraint("CK_ReportSnapshot_HashFormat", "[HashFormat] IN ('current', 'legacy')");
                    table.ForeignKey(
                        name: "FK_Snap_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Snap_Version",
                        column: x => x.ReportVersionId,
                        principalSchema: "rpt",
                        principalTable: "ReportVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportRow",
                schema: "rpt",
                columns: table => new
                {
                    SnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    RowNo = table.Column<int>(type: "int", nullable: false),
                    ColumnCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ValueDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ValueNumeric = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true),
                    ValueString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRow", x => new { x.SnapshotId, x.RowNo, x.ColumnCode });
                    table.ForeignKey(
                        name: "FK_RepRow_Snap",
                        column: x => x.SnapshotId,
                        principalSchema: "rpt",
                        principalTable: "ReportSnapshot",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_ReportDef",
                schema: "rpt",
                table: "ReportDef",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReportSnapshot_Current",
                schema: "rpt",
                table: "ReportSnapshot",
                columns: new[] { "ReportVersionId", "ProjectId", "PeriodKey" },
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UQ_ReportVersion",
                schema: "rpt",
                table: "ReportVersion",
                columns: new[] { "ReportDefId", "Version" },
                unique: true);
        }
    }
}
