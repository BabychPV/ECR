using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Stage4Dictionaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dic");

            migrationBuilder.CreateTable(
                name: "RegistryEntry",
                schema: "dic",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ParentEntryId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryEntry", x => x.Id);
                    table.CheckConstraint("CK_RegEntry_Period", "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom <= ValidTo");
                    table.ForeignKey(
                        name: "FK_RegEntry_Def",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegEntry_Parent",
                        column: x => x.ParentEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistryEntryLink",
                schema: "dic",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeftEntryId = table.Column<int>(type: "int", nullable: false),
                    RightEntryId = table.Column<int>(type: "int", nullable: false),
                    LinkKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryEntryLink", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegLink_Left",
                        column: x => x.LeftEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegLink_Right",
                        column: x => x.RightEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistryExternalKey",
                schema: "dic",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryEntryId = table.Column<int>(type: "int", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExternalPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryExternalKey", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegExtKey_Entry",
                        column: x => x.RegistryEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistryValue",
                schema: "dic",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryEntryId = table.Column<int>(type: "int", nullable: false),
                    RegistryFieldDefId = table.Column<int>(type: "int", nullable: false),
                    ValueNumeric = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ValueString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ValueDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ValueBool = table.Column<bool>(type: "bit", nullable: true),
                    ValueRefEntryId = table.Column<int>(type: "int", nullable: true),
                    ValueUnitId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegValue_Entry",
                        column: x => x.RegistryEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegValue_Field",
                        column: x => x.RegistryFieldDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryFieldDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegValue_Ref",
                        column: x => x.ValueRefEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegValue_Unit",
                        column: x => x.ValueUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistryEntry_Lookup",
                schema: "dic",
                table: "RegistryEntry",
                columns: new[] { "RegistryDefId", "IsActive", "IsDeleted" })
                .Annotation("SqlServer:Include", new[] { "Code", "Ordinal", "ValidFrom", "ValidTo" });

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryEntry",
                schema: "dic",
                table: "RegistryEntry",
                columns: new[] { "RegistryDefId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryEntryLink",
                schema: "dic",
                table: "RegistryEntryLink",
                columns: new[] { "LeftEntryId", "RightEntryId", "LinkKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryExternalKey",
                schema: "dic",
                table: "RegistryExternalKey",
                columns: new[] { "DataSourceId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryValue",
                schema: "dic",
                table: "RegistryValue",
                columns: new[] { "RegistryEntryId", "RegistryFieldDefId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistryEntryLink",
                schema: "dic");

            migrationBuilder.DropTable(
                name: "RegistryExternalKey",
                schema: "dic");

            migrationBuilder.DropTable(
                name: "RegistryValue",
                schema: "dic");

            migrationBuilder.DropTable(
                name: "RegistryEntry",
                schema: "dic");
        }
    }
}
