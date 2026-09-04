using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Stage4Calculations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalculationRun",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: true),
                    TriggeredByUserId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ModulesProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CR_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Methodology",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Group = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Methodology", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalculationInput",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    CalculationRunId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    SourceRowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ArgumentCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ValueString = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    UnitId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationInput", x => new { x.PeriodKey, x.Id });
                    table.ForeignKey(
                        name: "FK_CIn_Run",
                        column: x => x.CalculationRunId,
                        principalSchema: "calc",
                        principalTable: "CalculationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationStep",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    CalculationRunId = table.Column<long>(type: "bigint", nullable: false),
                    ResultId = table.Column<long>(type: "bigint", nullable: true),
                    StepOrder = table.Column<int>(type: "int", nullable: false),
                    StepCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Expression = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Value = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    TraceJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationStep", x => new { x.PeriodKey, x.Id });
                    table.ForeignKey(
                        name: "FK_CStep_Run",
                        column: x => x.CalculationRunId,
                        principalSchema: "calc",
                        principalTable: "CalculationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MethodologyVersion",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    NumericMode = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    CalendarMode = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    TraceLevel = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1),
                    ContentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    PublishedByUserId = table.Column<int>(type: "int", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ChangeReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyVersion", x => x.Id);
                    table.CheckConstraint("CK_MV_FourEyes", "PublishedByUserId IS NULL OR PublishedByUserId <> CreatedByUserId");
                    table.CheckConstraint("CK_MV_Published", "Status <> 1 OR (PublishedAt IS NOT NULL AND EffectiveFrom IS NOT NULL AND ChangeReason IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_MV_Methodology",
                        column: x => x.MethodologyId,
                        principalSchema: "calc",
                        principalTable: "Methodology",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationResult",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    CalculationRunId = table.Column<long>(type: "bigint", nullable: false),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    SourceRowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OutputCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    SubstanceEntryId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationResult", x => new { x.PeriodKey, x.Id });
                    table.ForeignKey(
                        name: "FK_CRes_MV",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CRes_Run",
                        column: x => x.CalculationRunId,
                        principalSchema: "calc",
                        principalTable: "CalculationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CRes_Unit",
                        column: x => x.UnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MethodologyConstant",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    SubstanceEntryId = table.Column<int>(type: "int", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyConstant", x => x.Id);
                    table.CheckConstraint("CK_MC_Period", "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom <= ValidTo");
                    table.ForeignKey(
                        name: "FK_MC_Substance",
                        column: x => x.SubstanceEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MC_Unit",
                        column: x => x.UnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MC_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MethodologyFormula",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Expression = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EvaluationOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    OutputUnitId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyFormula", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MF_Unit",
                        column: x => x.OutputUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MF_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MethodologyOutput",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyOutput", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MO_Unit",
                        column: x => x.UnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MO_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MethodologyRule",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MatchJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MR_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MethodologySubstance",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    SubstanceEntryId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologySubstance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MS_Substance",
                        column: x => x.SubstanceEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MS_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScriptVersion",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CompiledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CompilerDiagnostics = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HasGreenTest = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ContentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SV_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationResult_Lookup",
                schema: "calc",
                table: "CalculationResult",
                columns: new[] { "PeriodKey", "DocumentId", "MethodologyVersionId", "OutputCode" })
                .Annotation("SqlServer:Include", new[] { "Value", "UnitId", "SubstanceEntryId", "SourceRowKey" });

            migrationBuilder.CreateIndex(
                name: "UQ_Methodology",
                schema: "calc",
                table: "Methodology",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyFormula",
                schema: "calc",
                table: "MethodologyFormula",
                columns: new[] { "MethodologyVersionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyOutput",
                schema: "calc",
                table: "MethodologyOutput",
                columns: new[] { "MethodologyVersionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyRule",
                schema: "calc",
                table: "MethodologyRule",
                columns: new[] { "MethodologyVersionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologySubstance",
                schema: "calc",
                table: "MethodologySubstance",
                columns: new[] { "MethodologyVersionId", "SubstanceEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyVersion",
                schema: "calc",
                table: "MethodologyVersion",
                columns: new[] { "MethodologyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_ScriptVersion",
                schema: "calc",
                table: "ScriptVersion",
                column: "MethodologyVersionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalculationInput",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "CalculationResult",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "CalculationStep",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "MethodologyConstant",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "MethodologyFormula",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "MethodologyOutput",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "MethodologyRule",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "MethodologySubstance",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "ScriptVersion",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "CalculationRun",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "MethodologyVersion",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "Methodology",
                schema: "calc");
        }
    }
}
