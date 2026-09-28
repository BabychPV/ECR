using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HSE301M2RowWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RowWindowMap",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    TargetColumnDefId = table.Column<int>(type: "int", nullable: false),
                    StartColumnDefId = table.Column<int>(type: "int", nullable: false),
                    EndColumnDefId = table.Column<int>(type: "int", nullable: false),
                    SelectorColumnDefId = table.Column<int>(type: "int", nullable: true),
                    Summary = table.Column<byte>(type: "tinyint", nullable: false),
                    IsStep = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RWM_Step"),
                    MaxGapSeconds = table.Column<int>(type: "int", nullable: true),
                    TargetUnitId = table.Column<int>(type: "int", nullable: false),
                    MinPercentGood = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false, defaultValue: 95m)
                        .Annotation("Relational:DefaultConstraintName", "DF_RWM_MinGood"),
                    RefetchWithinDays = table.Column<int>(type: "int", nullable: false, defaultValue: 7)
                        .Annotation("Relational:DefaultConstraintName", "DF_RWM_Refetch"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_RWM_Act"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RowWindowMap", x => x.Id);
                    table.CheckConstraint("CK_RWM_Policy", "MinPercentGood BETWEEN 0 AND 100 AND RefetchWithinDays BETWEEN 0 AND 366 AND (MaxGapSeconds IS NULL OR MaxGapSeconds > 0)");
                    table.CheckConstraint("CK_RWM_Summary", "Summary BETWEEN 0 AND 4");
                    table.CheckConstraint("CK_RWM_Window", "StartColumnDefId <> EndColumnDefId");
                    table.ForeignKey(
                        name: "FK_RWM_End",
                        columns: x => new { x.TableDefId, x.EndColumnDefId },
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumns: new[] { "TableDefId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWM_Selector",
                        columns: x => new { x.TableDefId, x.SelectorColumnDefId },
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumns: new[] { "TableDefId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWM_Start",
                        columns: x => new { x.TableDefId, x.StartColumnDefId },
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumns: new[] { "TableDefId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWM_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWM_Target",
                        columns: x => new { x.TableDefId, x.TargetColumnDefId },
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumns: new[] { "TableDefId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWM_Unit",
                        column: x => x.TargetUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RowWindowSource",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RowWindowMapId = table.Column<int>(type: "int", nullable: false),
                    SelectorValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceUnitId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RowWindowSource", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RWS_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWS_Map",
                        column: x => x.RowWindowMapId,
                        principalSchema: "ext",
                        principalTable: "RowWindowMap",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWS_Unit",
                        column: x => x.SourceUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RowWindowValue",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    TableInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    RowWindowMapId = table.Column<int>(type: "int", nullable: false),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FromUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Summary = table.Column<byte>(type: "tinyint", nullable: false),
                    ComputedBy = table.Column<byte>(type: "tinyint", nullable: false),
                    ValueSource = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true),
                    SourceUnitSymbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ValueTarget = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true),
                    TargetUnitId = table.Column<int>(type: "int", nullable: false),
                    ConversionFactor = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true),
                    PointCount = table.Column<int>(type: "int", nullable: false),
                    PercentGood = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    RetrievedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_RWV_Current")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RowWindowValue", x => new { x.PeriodKey, x.Id });
                    table.CheckConstraint("CK_RWV_Kinds", "Summary BETWEEN 0 AND 4 AND ComputedBy BETWEEN 0 AND 1");
                    table.CheckConstraint("CK_RWV_Numbers", "PointCount >= 0 AND (PercentGood IS NULL OR PercentGood BETWEEN 0 AND 100)");
                    table.CheckConstraint("CK_RWV_Status", "Status IN (N'Fetched', N'Partial', N'NoData', N'KeptManual', N'SourceError', N'InvalidWindow', N'NotApplicable')");
                    table.ForeignKey(
                        name: "FK_RWV_Column",
                        column: x => x.ColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWV_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWV_Map",
                        column: x => x.RowWindowMapId,
                        principalSchema: "ext",
                        principalTable: "RowWindowMap",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RWV_Unit",
                        column: x => x.TargetUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_RowWindowMap_Target",
                schema: "ext",
                table: "RowWindowMap",
                columns: new[] { "TableDefId", "TargetColumnDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RowWindowSource",
                schema: "ext",
                table: "RowWindowSource",
                columns: new[] { "RowWindowMapId", "SelectorValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RowWindowValue_Current",
                schema: "ext",
                table: "RowWindowValue",
                columns: new[] { "PeriodKey", "TableInstanceId", "RowKey", "ColumnDefId" },
                unique: true,
                filter: "[IsCurrent] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RowWindowSource",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "RowWindowValue",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "RowWindowMap",
                schema: "ext");
        }
    }
}
