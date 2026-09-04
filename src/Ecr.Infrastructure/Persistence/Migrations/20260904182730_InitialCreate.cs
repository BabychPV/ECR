using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wf");

            migrationBuilder.EnsureSchema(
                name: "cfg");

            migrationBuilder.EnsureSchema(
                name: "doc");

            migrationBuilder.EnsureSchema(
                name: "uom");

            migrationBuilder.EnsureSchema(
                name: "sec");

            migrationBuilder.CreateSequence(
                name: "TableInstanceSeq",
                schema: "doc");

            migrationBuilder.CreateSequence(
                name: "TableRowSeq",
                schema: "doc");

            migrationBuilder.CreateTable(
                name: "ApprovalState",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    SheetDefId = table.Column<int>(type: "int", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CurrentStepId = table.Column<int>(type: "int", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SubmittedByUserId = table.Column<int>(type: "int", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    RejectedReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReopenedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReopenedByUserId = table.Column<int>(type: "int", nullable: true),
                    ReopenReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalState", x => x.Id);
                    table.CheckConstraint("CK_ApprState_Reopen", "ReopenedAt IS NULL OR ReopenReason IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "CalculationBinding",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    MethodologyId = table.Column<int>(type: "int", nullable: false),
                    OutputCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MatchJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_CalcBind_Act")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationBinding", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversion",
                schema: "uom",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FromUnitId = table.Column<int>(type: "int", nullable: false),
                    ToUnitId = table.Column<int>(type: "int", nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Offset = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false, defaultValue: 0m)
                        .Annotation("Relational:DefaultConstraintName", "DF_Conv_Offset"),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversion", x => x.Id);
                    table.CheckConstraint("CK_Conv_Note", "Kind <> 1 OR Note IS NOT NULL");
                    table.CheckConstraint("CK_Conv_NotSelf", "FromUnitId <> ToUnitId");
                });

            migrationBuilder.CreateTable(
                name: "Dimension",
                schema: "uom",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BaseUnitId = table.Column<int>(type: "int", nullable: true),
                    IsDerived = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Dim_Derived"),
                    NumeratorDimensionId = table.Column<byte>(type: "tinyint", nullable: true),
                    DenominatorDimensionId = table.Column<byte>(type: "tinyint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dimension", x => x.Id);
                    table.CheckConstraint("CK_Dim_Derived", "IsDerived = 0 OR (NumeratorDimensionId IS NOT NULL AND DenominatorDimensionId IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "PasswordPolicy",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MinLength = table.Column<int>(type: "int", nullable: false, defaultValue: 12)
                        .Annotation("Relational:DefaultConstraintName", "DF_PwdP_Len"),
                    MaxFailedAttempts = table.Column<int>(type: "int", nullable: false, defaultValue: 5)
                        .Annotation("Relational:DefaultConstraintName", "DF_PwdP_Att"),
                    LockoutMinutes = table.Column<int>(type: "int", nullable: false, defaultValue: 15)
                        .Annotation("Relational:DefaultConstraintName", "DF_PwdP_Lck"),
                    RequireUpper = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_PwdP_Up"),
                    RequireDigit = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_PwdP_Dig"),
                    RequireSpecial = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_PwdP_Spc"),
                    ExpirationDays = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordPolicy", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeriodAccessRuleDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    SheetDefId = table.Column<int>(type: "int", nullable: true),
                    TableDefId = table.Column<int>(type: "int", nullable: true),
                    RoleId = table.Column<int>(type: "int", nullable: true),
                    FromSequence = table.Column<byte>(type: "tinyint", nullable: true),
                    ToSequence = table.Column<byte>(type: "tinyint", nullable: true),
                    OnOutOfWindow = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeriodAccessRuleDef", x => x.Id);
                    table.CheckConstraint("CK_PAR_Range", "FromSequence IS NULL OR ToSequence IS NULL OR FromSequence <= ToSequence");
                    table.CheckConstraint("CK_PAR_Target", "SheetDefId IS NOT NULL OR TableDefId IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "PeriodPolicy",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OpenOffsetDays = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_PP_Open"),
                    GraceOffsetDays = table.Column<int>(type: "int", nullable: false, defaultValue: 15)
                        .Annotation("Relational:DefaultConstraintName", "DF_PP_Grace"),
                    HardCloseOffsetDays = table.Column<int>(type: "int", nullable: false, defaultValue: 45)
                        .Annotation("Relational:DefaultConstraintName", "DF_PP_Hard"),
                    YearGraceOffsetDays = table.Column<int>(type: "int", nullable: false, defaultValue: 45)
                        .Annotation("Relational:DefaultConstraintName", "DF_PP_Year")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeriodPolicy", x => x.Id);
                    table.CheckConstraint("CK_PP_Order", "GraceOffsetDays <= HardCloseOffsetDays");
                });

            migrationBuilder.CreateTable(
                name: "Permission",
                schema: "sec",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Group = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDangerous = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Perm_Dang")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permission", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Project",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Year = table.Column<short>(type: "smallint", nullable: true),
                    TagsJson = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    PeriodKind = table.Column<byte>(type: "tinyint", nullable: false),
                    PeriodPolicyId = table.Column<int>(type: "int", nullable: false),
                    YearGraceOffsetDays = table.Column<int>(type: "int", nullable: false, defaultValue: 45)
                        .Annotation("Relational:DefaultConstraintName", "DF_Project_YearGrace"),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, defaultValue: "Central Asia Standard Time")
                        .Annotation("Relational:DefaultConstraintName", "DF_Project_Tz"),
                    CurrentPeriodMode = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "0")
                        .Annotation("Relational:DefaultConstraintName", "DF_Project_CPMode"),
                    CurrentPeriodId = table.Column<int>(type: "int", nullable: true),
                    CurrentPeriodPinnedReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CurrentPeriodChangedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CurrentPeriodChangedByUserId = table.Column<int>(type: "int", nullable: true),
                    ExternalSettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    IsArchiving = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Project_Arch"),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Project", x => x.Id);
                    table.CheckConstraint("CK_Project_Period", "PeriodStart <= PeriodEnd");
                    table.CheckConstraint("CK_Project_Pinned", "CurrentPeriodMode <> 1 OR (CurrentPeriodId IS NOT NULL AND CurrentPeriodPinnedReason IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "RegistryDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsTemporal = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegDef_Temp"),
                    SourceKind = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "2")
                        .Annotation("Relational:DefaultConstraintName", "DF_RegDef_Src"),
                    DataRevision = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegDef_Rev"),
                    DefinitionVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegDef_Ver"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegDef_Act")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryDef", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Role",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Role_Built"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Role_Act")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetGroupRule",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    SheetGroup = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RuleKind = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetGroup = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetGroupRule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StyleDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FontName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FontSize = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    IsBold = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Style_Bold"),
                    IsItalic = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Style_Ital"),
                    ForegroundArgb = table.Column<int>(type: "int", nullable: true),
                    BackgroundArgb = table.Column<int>(type: "int", nullable: true),
                    BorderJson = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HorizontalAlign = table.Column<byte>(type: "tinyint", nullable: true),
                    VerticalAlign = table.Column<byte>(type: "tinyint", nullable: true),
                    WrapText = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Style_Wrap"),
                    NumberFormat = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StyleDef", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TableInstance",
                schema: "doc",
                columns: table => new
                {
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableInstance", x => new { x.PeriodKey, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "TableRelationDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceTableDefId = table.Column<int>(type: "int", nullable: false),
                    TargetTableDefId = table.Column<int>(type: "int", nullable: false),
                    RelationKind = table.Column<byte>(type: "tinyint", nullable: false),
                    MatchJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MapJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OnSourceChange = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "0")
                        .Annotation("Relational:DefaultConstraintName", "DF_Rel_OnChange"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Rel_Active")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableRelationDef", x => x.Id);
                    table.CheckConstraint("CK_Rel_NotSelf", "SourceTableDefId <> TargetTableDefId");
                });

            migrationBuilder.CreateTable(
                name: "Template",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TagsJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Template_Active"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Template", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Provider = table.Column<byte>(type: "tinyint", nullable: false),
                    WindowsSid = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PasswordPolicyId = table.Column<int>(type: "int", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_User_Failed"),
                    LockedUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_User_MustChg"),
                    IsBootstrapAdmin = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_User_Bootstrap"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_User_Active"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                    table.CheckConstraint("CK_User_Provider", "(Provider = 0 AND WindowsSid IS NOT NULL AND PasswordHash IS NULL) OR (Provider = 1 AND PasswordHash IS NOT NULL AND WindowsSid IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Unit",
                schema: "uom",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SymbolL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DimensionId = table.Column<byte>(type: "tinyint", nullable: false),
                    IsBase = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Unit_Base"),
                    FactorToBase = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false, defaultValue: 1m)
                        .Annotation("Relational:DefaultConstraintName", "DF_Unit_Factor"),
                    OffsetToBase = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false, defaultValue: 0m)
                        .Annotation("Relational:DefaultConstraintName", "DF_Unit_Offset"),
                    NumeratorUnitId = table.Column<int>(type: "int", nullable: true),
                    DenominatorUnitId = table.Column<int>(type: "int", nullable: true),
                    DisplayFormat = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Unit_Active")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Unit", x => x.Id);
                    table.CheckConstraint("CK_Unit_Base", "IsBase = 0 OR (FactorToBase = 1 AND OffsetToBase = 0)");
                    table.CheckConstraint("CK_Unit_Factor", "FactorToBase <> 0");
                    table.ForeignKey(
                        name: "FK_Unit_Dimension",
                        column: x => x.DimensionId,
                        principalSchema: "uom",
                        principalTable: "Dimension",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Document",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    BusinessKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ModifiedByUserId = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Document", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Document_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Period",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<byte>(type: "tinyint", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    ComputedOpenAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ComputedGraceAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ComputedCloseAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReopenedUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReopenReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    StateChangedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Period", x => x.Id);
                    table.CheckConstraint("CK_Period_Range", "PeriodStart <= PeriodEnd");
                    table.CheckConstraint("CK_Period_Seq", "Sequence BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_Period_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistryFieldDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DataType = table.Column<byte>(type: "tinyint", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegField_Req"),
                    IsKey = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegField_Key"),
                    UnitId = table.Column<int>(type: "int", nullable: true),
                    RefRegistryDefId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryFieldDef", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegField_Registry",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResourceGrant",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    ResourceKind = table.Column<byte>(type: "tinyint", nullable: false),
                    ResourceId = table.Column<int>(type: "int", nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: false),
                    IsDeny = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Grant_Deny")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceGrant", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Grant_Role",
                        column: x => x.RoleId,
                        principalSchema: "sec",
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TableRow",
                schema: "doc",
                columns: table => new
                {
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    TableInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowDefId = table.Column<int>(type: "int", nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_TableRow_Del"),
                    IsOrphaned = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_TableRow_Orph"),
                    OrphanedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableRow", x => new { x.PeriodKey, x.Id });
                    table.ForeignKey(
                        name: "FK_TableRow_Instance",
                        columns: x => new { x.PeriodKey, x.TableInstanceId },
                        principalSchema: "doc",
                        principalTable: "TableInstance",
                        principalColumns: new[] { "PeriodKey", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemplateVersion",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ClonedFromVersionId = table.Column<int>(type: "int", nullable: true),
                    PresentationRevision = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_TV_PresRev"),
                    SourceWorkbookHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PublishedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateVersion", x => x.Id);
                    table.CheckConstraint("CK_TV_Published", "Status <> 1 OR (PublishedAt IS NOT NULL AND PublishedByUserId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_TemplateVersion_Template",
                        column: x => x.TemplateId,
                        principalSchema: "cfg",
                        principalTable: "Template",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentSheet",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    SheetDefId = table.Column<int>(type: "int", nullable: false),
                    IsIncluded = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_DocSheet_Inc")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSheet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocSheet_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SheetDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SheetGroup = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_SheetDef_Mand"),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_SheetDef_Vis"),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_SheetDef_Del"),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetDef", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SheetDef_Version",
                        column: x => x.TemplateVersionId,
                        principalSchema: "cfg",
                        principalTable: "TemplateVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TableDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SheetDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    LayoutKind = table.Column<byte>(type: "tinyint", nullable: false),
                    RowMode = table.Column<byte>(type: "tinyint", nullable: false),
                    MaxDynamicRows = table.Column<int>(type: "int", nullable: true),
                    HeaderStyleId = table.Column<int>(type: "int", nullable: true),
                    StorageMode = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "0")
                        .Annotation("Relational:DefaultConstraintName", "DF_TableDef_Storage"),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_TableDef_Del"),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableDef", x => x.Id);
                    table.CheckConstraint("CK_TableDef_MaxRows", "RowMode = 0 OR MaxDynamicRows IS NULL OR MaxDynamicRows > 0");
                    table.ForeignKey(
                        name: "FK_TableDef_Sheet",
                        column: x => x.SheetDefId,
                        principalSchema: "cfg",
                        principalTable: "SheetDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ColumnDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HeaderL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    DataType = table.Column<byte>(type: "tinyint", nullable: false),
                    Precision = table.Column<byte>(type: "tinyint", nullable: true),
                    Scale = table.Column<byte>(type: "tinyint", nullable: true),
                    IsReadOnly = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_RO"),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_Req"),
                    IsHidden = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_Hid"),
                    IsMonthColumn = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_Mon"),
                    MonthNumber = table.Column<byte>(type: "tinyint", nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DisplayFormat = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LookupRegistryDefId = table.Column<int>(type: "int", nullable: true),
                    LookupFilter = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CascadeFromColumnId = table.Column<int>(type: "int", nullable: true),
                    UnitId = table.Column<int>(type: "int", nullable: true),
                    IsBusinessKey = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_BK"),
                    IsScopeField = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_Sc"),
                    IsIndexed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_Ix"),
                    StyleId = table.Column<int>(type: "int", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_ColumnDef_Del"),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnDef", x => x.Id);
                    table.UniqueConstraint("UQ_ColumnDef_ForFk", x => new { x.TableDefId, x.Id });
                    table.CheckConstraint("CK_ColumnDef_Lookup", "DataType <> 5 OR LookupRegistryDefId IS NOT NULL");
                    table.CheckConstraint("CK_ColumnDef_Month", "IsMonthColumn = 0 OR MonthNumber BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_ColumnDef_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FormulaDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<byte>(type: "tinyint", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: true),
                    RowDefId = table.Column<int>(type: "int", nullable: true),
                    Dialect = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "0")
                        .Annotation("Relational:DefaultConstraintName", "DF_Formula_Dialect"),
                    Expression = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EvaluationOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_Formula_Order"),
                    IsCrossSheet = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Formula_Cross"),
                    IsSnapshot = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Formula_Snap"),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Formula_Del"),
                    TableDefId1 = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormulaDef", x => x.Id);
                    table.CheckConstraint("CK_Formula_Scope", "(Scope = 0 AND ColumnDefId IS NOT NULL) OR (Scope = 1 AND RowDefId IS NOT NULL) OR (Scope = 2 AND ColumnDefId IS NOT NULL AND RowDefId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_FormulaDef_TableDef_TableDefId1",
                        column: x => x.TableDefId1,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Formula_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RowDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    LabelL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowKind = table.Column<byte>(type: "tinyint", nullable: false),
                    ParentRowDefId = table.Column<int>(type: "int", nullable: true),
                    IsReadOnly = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RowDef_RO"),
                    StyleId = table.Column<int>(type: "int", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RowDef_Del"),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RowDef", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RowDef_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ValidationRule",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    Scope = table.Column<byte>(type: "tinyint", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: true),
                    Expression = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    MessageL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_VRule_Active")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValidationRule_TableDef_TableDefId",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CellValue",
                schema: "doc",
                columns: table => new
                {
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    TableRowId = table.Column<long>(type: "bigint", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    ValueString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ValueNumeric = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ValueDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ValueBool = table.Column<bool>(type: "bit", nullable: true),
                    ValueRegistryEntryId = table.Column<int>(type: "int", nullable: true),
                    ValueUnitId = table.Column<int>(type: "int", nullable: true),
                    IsCalculated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_CellValue_Calc"),
                    IsEmpty = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_CellValue_Empty")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CellValue", x => new { x.PeriodKey, x.TableRowId, x.ColumnDefId });
                    table.CheckConstraint("CK_CellValue_Empty", "IsEmpty = 0 OR (ValueString IS NULL AND ValueNumeric IS NULL AND ValueDate IS NULL AND ValueBool IS NULL AND ValueRegistryEntryId IS NULL AND ValueUnitId IS NULL)");
                    table.ForeignKey(
                        name: "FK_CellValue_Column",
                        columns: x => new { x.TableDefId, x.ColumnDefId },
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumns: new[] { "TableDefId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CellValue_Row",
                        columns: x => new { x.PeriodKey, x.TableRowId },
                        principalSchema: "doc",
                        principalTable: "TableRow",
                        principalColumns: new[] { "PeriodKey", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FormulaDependency",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKind = table.Column<byte>(type: "tinyint", nullable: false),
                    FormulaDefId = table.Column<int>(type: "int", nullable: true),
                    BindingId = table.Column<int>(type: "int", nullable: true),
                    DependsOnKind = table.Column<byte>(type: "tinyint", nullable: false),
                    TableDefId = table.Column<int>(type: "int", nullable: true),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ColumnDefId = table.Column<int>(type: "int", nullable: true),
                    FilterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PeriodOffset = table.Column<short>(type: "smallint", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormulaDependency", x => x.Id);
                    table.CheckConstraint("CK_FDep_Source", "(SourceKind = 0 AND FormulaDefId IS NOT NULL) OR (SourceKind = 1 AND BindingId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_FDep_Formula",
                        column: x => x.FormulaDefId,
                        principalSchema: "cfg",
                        principalTable: "FormulaDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_ApprovalState",
                schema: "wf",
                table: "ApprovalState",
                columns: new[] { "DocumentId", "SheetDefId", "PeriodKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_CalculationBinding",
                schema: "cfg",
                table: "CalculationBinding",
                columns: new[] { "ColumnDefId", "MethodologyId", "OutputCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_ColumnDef",
                schema: "cfg",
                table: "ColumnDef",
                columns: new[] { "TableDefId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Conversion",
                schema: "uom",
                table: "Conversion",
                columns: new[] { "FromUnitId", "ToUnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Dimension_Code",
                schema: "uom",
                table: "Dimension",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Document",
                schema: "doc",
                table: "Document",
                columns: new[] { "ProjectId", "BusinessKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentSheet",
                schema: "doc",
                table: "DocumentSheet",
                columns: new[] { "DocumentId", "SheetDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FormulaDependency_Reverse",
                schema: "cfg",
                table: "FormulaDependency",
                columns: new[] { "TableDefId", "RowKey", "ColumnDefId" });

            migrationBuilder.CreateIndex(
                name: "UQ_PasswordPolicy",
                schema: "sec",
                table: "PasswordPolicy",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Period",
                schema: "doc",
                table: "Period",
                columns: new[] { "ProjectId", "PeriodKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PeriodPolicy",
                schema: "doc",
                table: "PeriodPolicy",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Project_Code",
                schema: "doc",
                table: "Project",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryDef",
                schema: "cfg",
                table: "RegistryDef",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryFieldDef",
                schema: "cfg",
                table: "RegistryFieldDef",
                columns: new[] { "RegistryDefId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_ResourceGrant",
                schema: "sec",
                table: "ResourceGrant",
                columns: new[] { "RoleId", "ResourceKind", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Role",
                schema: "sec",
                table: "Role",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RowDef",
                schema: "cfg",
                table: "RowDef",
                columns: new[] { "TableDefId", "RowKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_SheetDef",
                schema: "cfg",
                table: "SheetDef",
                columns: new[] { "TemplateVersionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_StyleDef",
                schema: "cfg",
                table: "StyleDef",
                columns: new[] { "TemplateVersionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_TableDef",
                schema: "cfg",
                table: "TableDef",
                columns: new[] { "SheetDefId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_TableInstance",
                schema: "doc",
                table: "TableInstance",
                columns: new[] { "PeriodKey", "DocumentId", "TableDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_TableRelationDef",
                schema: "cfg",
                table: "TableRelationDef",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_TableRow_Key",
                schema: "doc",
                table: "TableRow",
                columns: new[] { "PeriodKey", "TableInstanceId", "RowKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Template_Code",
                schema: "cfg",
                table: "Template",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_TemplateVersion",
                schema: "cfg",
                table: "TemplateVersion",
                columns: new[] { "TemplateId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Unit_Code",
                schema: "uom",
                table: "Unit",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Unit_BasePerDimension",
                schema: "uom",
                table: "Unit",
                column: "DimensionId",
                unique: true,
                filter: "[IsBase] = 1");

            migrationBuilder.CreateIndex(
                name: "UQ_User_Name",
                schema: "sec",
                table: "User",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_User_Bootstrap",
                schema: "sec",
                table: "User",
                column: "IsBootstrapAdmin",
                unique: true,
                filter: "[IsBootstrapAdmin] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_User_Sid",
                schema: "sec",
                table: "User",
                column: "WindowsSid",
                unique: true,
                filter: "[WindowsSid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_ValidationRule",
                schema: "cfg",
                table: "ValidationRule",
                columns: new[] { "TableDefId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalState",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "CalculationBinding",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "CellValue",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "Conversion",
                schema: "uom");

            migrationBuilder.DropTable(
                name: "DocumentSheet",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "FormulaDependency",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PasswordPolicy",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "Period",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "PeriodAccessRuleDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PeriodPolicy",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "Permission",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "RegistryFieldDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "ResourceGrant",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "RowDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "SheetGroupRule",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "StyleDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "TableRelationDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Unit",
                schema: "uom");

            migrationBuilder.DropTable(
                name: "User",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "ValidationRule",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "ColumnDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "TableRow",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "Document",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "FormulaDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "RegistryDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "Dimension",
                schema: "uom");

            migrationBuilder.DropTable(
                name: "TableInstance",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "Project",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "TableDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "SheetDef",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "TemplateVersion",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Template",
                schema: "cfg");

            migrationBuilder.DropSequence(
                name: "TableInstanceSeq",
                schema: "doc");

            migrationBuilder.DropSequence(
                name: "TableRowSeq",
                schema: "doc");
        }
    }
}
