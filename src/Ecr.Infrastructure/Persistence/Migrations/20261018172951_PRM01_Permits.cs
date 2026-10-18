using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PRM01_Permits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PermitTypeDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<byte>(type: "tinyint", nullable: false),
                    HeaderRegistryDefId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                        .Annotation("Relational:DefaultConstraintName", "DF_PermitType_Ver"),
                    Status = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "0")
                        .Annotation("Relational:DefaultConstraintName", "DF_PermitType_Status"),
                    PublishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PublishedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitTypeDef", x => x.Id);
                    table.CheckConstraint("CK_PermitType_Category", "Category BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_PermitType_Published", "Status <> 1 OR (PublishedAt IS NOT NULL AND PublishedByUserId IS NOT NULL)");
                    table.CheckConstraint("CK_PermitType_Status", "Status BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_PermitType_HeaderReg",
                        column: x => x.HeaderRegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitType_PublishedBy",
                        column: x => x.PublishedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitFieldMeta",
                schema: "cfg",
                columns: table => new
                {
                    RegistryFieldDefId = table.Column<int>(type: "int", nullable: false),
                    PermitTypeDefId = table.Column<int>(type: "int", nullable: false),
                    GroupCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Precision = table.Column<byte>(type: "tinyint", nullable: true),
                    Scale = table.Column<byte>(type: "tinyint", nullable: true),
                    ListScope = table.Column<byte>(type: "tinyint", nullable: true),
                    ListRegistryDefId = table.Column<int>(type: "int", nullable: true),
                    ListSiblingTableCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitFieldMeta", x => x.RegistryFieldDefId);
                    table.CheckConstraint("CK_PermitField_List", "(ListScope IS NULL AND ListRegistryDefId IS NULL AND ListSiblingTableCode IS NULL) OR (ListScope IS NOT NULL AND ListScope IN (0, 1) AND ListRegistryDefId IS NOT NULL AND ListSiblingTableCode IS NULL) OR (ListScope IS NOT NULL AND ListScope = 2 AND ListSiblingTableCode IS NOT NULL AND ListRegistryDefId IS NULL)");
                    table.CheckConstraint("CK_PermitField_Num", "(Precision IS NULL OR Precision BETWEEN 1 AND 38) AND (Scale IS NULL OR (Precision IS NOT NULL AND Scale <= Precision))");
                    table.CheckConstraint("CK_PermitField_Scope", "ListScope IS NULL OR ListScope BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_PermitField_Field",
                        column: x => x.RegistryFieldDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryFieldDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitField_ListReg",
                        column: x => x.ListRegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitField_Type",
                        column: x => x.PermitTypeDefId,
                        principalSchema: "cfg",
                        principalTable: "PermitTypeDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitTypeTable",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PermitTypeDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitTypeTable", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitTypeTable_Reg",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitTypeTable_Type",
                        column: x => x.PermitTypeDefId,
                        principalSchema: "cfg",
                        principalTable: "PermitTypeDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitSheetBinding",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    SheetTableDefId = table.Column<int>(type: "int", nullable: false),
                    PermitTypeDefId = table.Column<int>(type: "int", nullable: false),
                    PermitTypeTableId = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<byte>(type: "tinyint", nullable: false),
                    SelectionRule = table.Column<byte>(type: "tinyint", nullable: false),
                    HeaderFieldDefId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_PermitBinding_Act"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitSheetBinding", x => x.Id);
                    table.CheckConstraint("CK_PermitBinding_Header", "(SelectionRule = 1 AND HeaderFieldDefId IS NOT NULL) OR (SelectionRule <> 1 AND HeaderFieldDefId IS NULL)");
                    table.CheckConstraint("CK_PermitBinding_Mode", "Mode BETWEEN 0 AND 1");
                    table.CheckConstraint("CK_PermitBinding_Rule", "SelectionRule BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_PermitBinding_Header",
                        column: x => x.HeaderFieldDefId,
                        principalSchema: "cfg",
                        principalTable: "HeaderFieldDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitBinding_Sheet",
                        column: x => x.SheetTableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitBinding_TV",
                        column: x => x.TemplateVersionId,
                        principalSchema: "cfg",
                        principalTable: "TemplateVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitBinding_Table",
                        column: x => x.PermitTypeTableId,
                        principalSchema: "cfg",
                        principalTable: "PermitTypeTable",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitBinding_Type",
                        column: x => x.PermitTypeDefId,
                        principalSchema: "cfg",
                        principalTable: "PermitTypeDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitSheetBindingColumn",
                schema: "cfg",
                columns: table => new
                {
                    BindingId = table.Column<int>(type: "int", nullable: false),
                    PermitFieldCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    SheetColumnDefId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitSheetBindingColumn", x => new { x.BindingId, x.PermitFieldCode });
                    table.CheckConstraint("CK_PermitBindCol_Role", "Role BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_PermitBindCol_Binding",
                        column: x => x.BindingId,
                        principalSchema: "cfg",
                        principalTable: "PermitSheetBinding",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitBindCol_Column",
                        column: x => x.SheetColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PermitFieldMeta_Type",
                schema: "cfg",
                table: "PermitFieldMeta",
                column: "PermitTypeDefId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitSheetBinding_Table",
                schema: "cfg",
                table: "PermitSheetBinding",
                column: "PermitTypeTableId");

            migrationBuilder.CreateIndex(
                name: "UQ_PermitSheetBinding_Code",
                schema: "cfg",
                table: "PermitSheetBinding",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PermitSheetBinding_Sheet",
                schema: "cfg",
                table: "PermitSheetBinding",
                columns: new[] { "TemplateVersionId", "SheetTableDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PermitBindCol_Column",
                schema: "cfg",
                table: "PermitSheetBindingColumn",
                columns: new[] { "BindingId", "SheetColumnDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PermitTypeDef",
                schema: "cfg",
                table: "PermitTypeDef",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PermitTypeTable",
                schema: "cfg",
                table: "PermitTypeTable",
                columns: new[] { "PermitTypeDefId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PermitTypeTable_Registry",
                schema: "cfg",
                table: "PermitTypeTable",
                column: "RegistryDefId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermitFieldMeta",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PermitSheetBindingColumn",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PermitSheetBinding",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PermitTypeTable",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PermitTypeDef",
                schema: "cfg");
        }
    }
}
