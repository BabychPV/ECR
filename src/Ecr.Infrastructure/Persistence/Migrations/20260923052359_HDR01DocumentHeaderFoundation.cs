using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HDR01DocumentHeaderFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HeaderFieldDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LabelL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    DataType = table.Column<byte>(type: "tinyint", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_HeaderFieldDef_Req"),
                    LookupRegistryDefId = table.Column<int>(type: "int", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_HeaderFieldDef_Del"),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeaderFieldDef", x => x.Id);
                    table.CheckConstraint("CK_HeaderFieldDef_Lookup", "DataType <> 5 OR LookupRegistryDefId IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_HeaderFieldDef_Registry",
                        column: x => x.LookupRegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HeaderFieldDef_Version",
                        column: x => x.TemplateVersionId,
                        principalSchema: "cfg",
                        principalTable: "TemplateVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentHeaderValue",
                schema: "doc",
                columns: table => new
                {
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    HeaderFieldDefId = table.Column<int>(type: "int", nullable: false),
                    ValueString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ValueNumeric = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true),
                    ValueDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ValueBool = table.Column<bool>(type: "bit", nullable: true),
                    ValueRegistryEntryId = table.Column<int>(type: "int", nullable: true),
                    ValueUnitId = table.Column<int>(type: "int", nullable: true),
                    IsEmpty = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_DocumentHeaderValue_Empty")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentHeaderValue", x => new { x.DocumentId, x.HeaderFieldDefId });
                    table.CheckConstraint("CK_DocumentHeaderValue_Empty", "IsEmpty = 0 OR (ValueString IS NULL AND ValueNumeric IS NULL AND ValueDate IS NULL AND ValueBool IS NULL AND ValueRegistryEntryId IS NULL AND ValueUnitId IS NULL)");
                    table.ForeignKey(
                        name: "FK_DocumentHeaderValue_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentHeaderValue_Entry",
                        column: x => x.ValueRegistryEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentHeaderValue_Field",
                        column: x => x.HeaderFieldDefId,
                        principalSchema: "cfg",
                        principalTable: "HeaderFieldDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentHeaderValue_Unit",
                        column: x => x.ValueUnitId,
                        principalSchema: "uom",
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_HeaderFieldDef",
                schema: "cfg",
                table: "HeaderFieldDef",
                columns: new[] { "TemplateVersionId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentHeaderValue",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "HeaderFieldDef",
                schema: "cfg");
        }
    }
}
