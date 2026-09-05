using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Stage5Integration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "itg");

            migrationBuilder.EnsureSchema(
                name: "ext");

            migrationBuilder.EnsureSchema(
                name: "rpt");

            migrationBuilder.CreateTable(
                name: "ArchiveRun",
                schema: "itg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FromPeriodKey = table.Column<int>(type: "int", nullable: false),
                    ToPeriodKey = table.Column<int>(type: "int", nullable: false),
                    LastDonePeriodKey = table.Column<int>(type: "int", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowsMoved = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ChecksumSourceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChecksumTargetJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TriggeredByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ARun_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsistencyRule",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceEntityId = table.Column<int>(type: "int", nullable: true),
                    Expression = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsistencyRule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataSource",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Transport = table.Column<byte>(type: "tinyint", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SecondaryEndpoint = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SecretName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Catalog = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MaxParallel = table.Column<int>(type: "int", nullable: false, defaultValue: 4),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSource", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentIndexValue",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    ValueString = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ValueNumeric = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ValueDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentIndexValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocIx_Column",
                        column: x => x.ColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocIx_Doc",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobProgress",
                schema: "itg",
                columns: table => new
                {
                    JobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    JobCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Percent = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Message = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobProgress", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "LegacyColumnMapping",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    LegacyFieldIndex = table.Column<int>(type: "int", nullable: true),
                    LegacyExcelColumn = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    AfAttributeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyColumnMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LCM_Column",
                        column: x => x.ColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegacyRowMapping",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RowDefId = table.Column<int>(type: "int", nullable: false),
                    AfAttributeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExcelRow = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyRowMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LRM_Row",
                        column: x => x.RowDefId,
                        principalSchema: "cfg",
                        principalTable: "RowDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegacySheetMapping",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SheetDefId = table.Column<int>(type: "int", nullable: false),
                    LegacyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacySheetMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LSM_Sheet",
                        column: x => x.SheetDefId,
                        principalSchema: "cfg",
                        principalTable: "SheetDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegacyTableMapping",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    EventFrameTemplate = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ElementTemplate = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ElementName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TemplateRow = table.Column<int>(type: "int", nullable: true),
                    RowOffsetBase = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyTableMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LTM_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceRun",
                schema: "itg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportDef",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRegulatory = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDef", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceEntity",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EntityPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SourceKind = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    RegistryDefId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEntity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SE_DataSource",
                        column: x => x.DataSourceId,
                        principalSchema: "ext",
                        principalTable: "DataSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SE_Registry",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportVersion",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportDefId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ColumnsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
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
                name: "CollectionRun",
                schema: "itg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    RangeFrom = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RangeTo = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PointsRetrieved = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsCatchUp = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TriggeredByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CRun_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectionSchedule",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LookbackDays = table.Column<int>(type: "int", nullable: false, defaultValue: 7),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    LastRunAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Watermark = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionSchedule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CS_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EntityFieldMap",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceUnitId = table.Column<int>(type: "int", nullable: true),
                    TargetKind = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetColumnDefId = table.Column<int>(type: "int", nullable: true),
                    TargetRegistryFieldDefId = table.Column<int>(type: "int", nullable: true),
                    TargetUnitId = table.Column<int>(type: "int", nullable: true),
                    TransformCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityFieldMap", x => x.Id);
                    table.CheckConstraint("CK_EFM_Target", "(TargetKind = 0 AND TargetColumnDefId IS NOT NULL) OR (TargetKind = 1 AND TargetRegistryFieldDefId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_EFM_Column",
                        column: x => x.TargetColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EFM_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EFM_Field",
                        column: x => x.TargetRegistryFieldDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryFieldDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EFM_SrcUnit",
                        column: x => x.SourceUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EFM_TgtUnit",
                        column: x => x.TargetUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RawDataPoint",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    SourcePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ValueNumeric = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ValueString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UnitId = table.Column<int>(type: "int", nullable: true),
                    Quality = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RetrievedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawDataPoint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RDP_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RDP_Unit",
                        column: x => x.UnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
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
                    ReportVersionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: true),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CalculationRunId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RowCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ContentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    BuiltAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    BuiltByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSnapshot", x => x.Id);
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
                name: "CollectionCoverage",
                schema: "itg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEntityId = table.Column<int>(type: "int", nullable: false),
                    CoveredFrom = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CoveredTo = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionCoverage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CCov_Entity",
                        column: x => x.SourceEntityId,
                        principalSchema: "ext",
                        principalTable: "SourceEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CCov_Run",
                        column: x => x.CollectionRunId,
                        principalSchema: "itg",
                        principalTable: "CollectionRun",
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
                    ValueString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ValueNumeric = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ValueDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
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
                name: "UQ_CollectionSchedule",
                schema: "ext",
                table: "CollectionSchedule",
                column: "SourceEntityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_ConsistencyRule",
                schema: "ext",
                table: "ConsistencyRule",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_DataSource",
                schema: "ext",
                table: "DataSource",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentIndexValue_Search",
                schema: "doc",
                table: "DocumentIndexValue",
                columns: new[] { "ColumnDefId", "ValueString" })
                .Annotation("SqlServer:Include", new[] { "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentIndexValue",
                schema: "doc",
                table: "DocumentIndexValue",
                columns: new[] { "DocumentId", "ColumnDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_EntityFieldMap",
                schema: "ext",
                table: "EntityFieldMap",
                columns: new[] { "SourceEntityId", "SourceField" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_LegacyColumnMapping",
                schema: "ext",
                table: "LegacyColumnMapping",
                column: "ColumnDefId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_LegacyRowMapping",
                schema: "ext",
                table: "LegacyRowMapping",
                column: "RowDefId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_LegacySheetMapping",
                schema: "ext",
                table: "LegacySheetMapping",
                column: "SheetDefId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_LegacyTableMapping",
                schema: "ext",
                table: "LegacyTableMapping",
                column: "TableDefId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RawDataPoint",
                schema: "ext",
                table: "RawDataPoint",
                columns: new[] { "SourceEntityId", "SourcePath", "Timestamp" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "UQ_SourceEntity",
                schema: "ext",
                table: "SourceEntity",
                columns: new[] { "DataSourceId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveRun",
                schema: "itg");

            migrationBuilder.DropTable(
                name: "CollectionCoverage",
                schema: "itg");

            migrationBuilder.DropTable(
                name: "CollectionSchedule",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "ConsistencyRule",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "DocumentIndexValue",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "EntityFieldMap",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "JobProgress",
                schema: "itg");

            migrationBuilder.DropTable(
                name: "LegacyColumnMapping",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "LegacyRowMapping",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "LegacySheetMapping",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "LegacyTableMapping",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "MaintenanceRun",
                schema: "itg");

            migrationBuilder.DropTable(
                name: "RawDataPoint",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "ReportRow",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "CollectionRun",
                schema: "itg");

            migrationBuilder.DropTable(
                name: "ReportSnapshot",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "SourceEntity",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "ReportVersion",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "DataSource",
                schema: "ext");

            migrationBuilder.DropTable(
                name: "ReportDef",
                schema: "rpt");
        }
    }
}
