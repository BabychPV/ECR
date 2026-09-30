using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HSE301M5SourceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceEventMap",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    FilterAttribute = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FilterScope = table.Column<byte>(type: "tinyint", nullable: true),
                    FilterValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    VolumeMode = table.Column<byte>(type: "tinyint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_SEM_Act"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEventMap", x => x.Id);
                    table.CheckConstraint("CK_SEM_Filter", "(FilterAttribute IS NULL AND FilterScope IS NULL AND FilterValue IS NULL) OR (FilterAttribute IS NOT NULL AND FilterScope BETWEEN 0 AND 1 AND FilterValue IS NOT NULL)");
                    table.CheckConstraint("CK_SEM_VolumeMode", "VolumeMode BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_SEM_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SEM_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SEM_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceEventFieldMap",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEventMapId = table.Column<int>(type: "int", nullable: false),
                    TargetColumnDefId = table.Column<int>(type: "int", nullable: false),
                    SourceAttribute = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AttributeScope = table.Column<byte>(type: "tinyint", nullable: false),
                    ValueKind = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceUnitId = table.Column<int>(type: "int", nullable: true),
                    TargetUnitId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEventFieldMap", x => x.Id);
                    table.CheckConstraint("CK_SEFM_Kinds", "AttributeScope BETWEEN 0 AND 1 AND ValueKind BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_SEFM_Column",
                        column: x => x.TargetColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SEFM_Map",
                        column: x => x.SourceEventMapId,
                        principalSchema: "ext",
                        principalTable: "SourceEventMap",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SEFM_SourceUnit",
                        column: x => x.SourceUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SEFM_TargetUnit",
                        column: x => x.TargetUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceEventLink",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEventMapId = table.Column<int>(type: "int", nullable: false),
                    SourceEventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: true),
                    TableInstanceId = table.Column<long>(type: "bigint", nullable: true),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EventName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    StartUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SourceModifiedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    KeptManualJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UnmappedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEventLink", x => x.Id);
                    table.CheckConstraint("CK_SEL_Row", "(PeriodKey IS NULL AND TableInstanceId IS NULL AND RowKey IS NULL) OR (PeriodKey IS NOT NULL AND TableInstanceId IS NOT NULL AND RowKey IS NOT NULL)");
                    table.CheckConstraint("CK_SEL_Status", "Status IN (N'Synced', N'Open', N'Missing', N'PeriodClosed', N'PeriodChanged', N'PeriodNotOpen', N'Unmapped', N'RowLimit')");
                    table.CheckConstraint("CK_SEL_StatusRow", "(Status NOT IN (N'Synced', N'Unmapped', N'Missing', N'PeriodChanged') OR TableInstanceId IS NOT NULL) AND (Status NOT IN (N'Open', N'PeriodNotOpen', N'RowLimit') OR TableInstanceId IS NULL)");
                    table.ForeignKey(
                        name: "FK_SEL_Map",
                        column: x => x.SourceEventMapId,
                        principalSchema: "ext",
                        principalTable: "SourceEventMap",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceEventValueMap",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEventFieldMapId = table.Column<int>(type: "int", nullable: false),
                    SourceValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RegistryEntryId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEventValueMap", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SEVM_Entry",
                        column: x => x.RegistryEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SEVM_Field",
                        column: x => x.SourceEventFieldMapId,
                        principalSchema: "ext",
                        principalTable: "SourceEventFieldMap",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_SEFM_Target",
                schema: "ext",
                table: "SourceEventFieldMap",
                columns: new[] { "SourceEventMapId", "TargetColumnDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SEL_Row",
                schema: "ext",
                table: "SourceEventLink",
                columns: new[] { "TableInstanceId", "RowKey" });

            migrationBuilder.CreateIndex(
                name: "UQ_SEL_Event",
                schema: "ext",
                table: "SourceEventLink",
                columns: new[] { "SourceEventMapId", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_SourceEventMap",
                schema: "ext",
                table: "SourceEventMap",
                columns: new[] { "SourceEntityId", "DocumentId", "TableDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_SEVM_Value",
                schema: "ext",
                table: "SourceEventValueMap",
                columns: new[] { "SourceEventFieldMapId", "SourceValue" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceEventLink",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "SourceEventValueMap",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "SourceEventFieldMap",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "SourceEventMap",
                schema: "ext");
        }
    }
}
