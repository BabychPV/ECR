using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class B80DocumentSourceBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentSourceBinding",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceTemplateId = table.Column<int>(type: "int", nullable: false),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    MappingKind = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceTableCodes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Policy = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceState = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0)
                        .Annotation("Relational:DefaultConstraintName", "DF_DocSrcBinding_State"),
                    ReadFromDocument = table.Column<bool>(type: "bit", nullable: false),
                    RecalculateDependents = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSourceBinding", x => x.Id);
                    table.CheckConstraint("CK_DocSrcBinding_Mapping", "[MappingKind] = 1");
                    table.CheckConstraint("CK_DocSrcBinding_Policy", "[Policy] BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_DocSrcBinding_State", "[SourceState] IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_DocSrcBinding_Registry",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocSrcBinding_Template",
                        column: x => x.SourceTemplateId,
                        principalSchema: "cfg",
                        principalTable: "Template",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocSrcBinding_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentSourceBindingConsumer",
                schema: "cfg",
                columns: table => new
                {
                    BindingId = table.Column<int>(type: "int", nullable: false),
                    TemplateId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSourceBindingConsumer", x => new { x.BindingId, x.TemplateId });
                    table.ForeignKey(
                        name: "FK_DocSrcBindingConsumer_Binding",
                        column: x => x.BindingId,
                        principalSchema: "cfg",
                        principalTable: "DocumentSourceBinding",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocSrcBindingConsumer_Template",
                        column: x => x.TemplateId,
                        principalSchema: "cfg",
                        principalTable: "Template",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSourceBinding_Registry",
                schema: "cfg",
                table: "DocumentSourceBinding",
                column: "RegistryDefId");

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentSourceBinding_Code",
                schema: "cfg",
                table: "DocumentSourceBinding",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentSourceBinding_Source",
                schema: "cfg",
                table: "DocumentSourceBinding",
                columns: new[] { "SourceTemplateId", "RegistryDefId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocSrcBindingConsumer_Template",
                schema: "cfg",
                table: "DocumentSourceBindingConsumer",
                column: "TemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentSourceBindingConsumer",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "DocumentSourceBinding",
                schema: "cfg");
        }
    }
}
