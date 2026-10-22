using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Групи типів дозволів (Permits, PRMG): <c>cfg.PermitGroup</c> і <c>cfg.PermitGroupMember</c>. Змін наявних таблиць немає.
    /// </summary>
    /// <remarks>
    /// ⛔ Відкат (<c>Down</c>) після того, як хоч одна колонка/поле шаблону вказує на прихований реєстр <c>PRMG_*</c>,
    /// заборонений: таблиці зникнуть, а цілі лишаться порожніми реєстрами без складу. Відкочувати лише порожню схему.
    /// </remarks>
    public partial class PRMG01_PermitGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PermitGroup",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DescriptionL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_PermitGroup_Act"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_PermitGroup_Rev"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitGroup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitGroup_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitGroup_Reg",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitGroup_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitGroupMember",
                schema: "cfg",
                columns: table => new
                {
                    PermitGroupId = table.Column<int>(type: "int", nullable: false),
                    PermitTypeDefId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitGroupMember", x => new { x.PermitGroupId, x.PermitTypeDefId });
                    table.ForeignKey(
                        name: "FK_PermitGroupMember_Group",
                        column: x => x.PermitGroupId,
                        principalSchema: "cfg",
                        principalTable: "PermitGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitGroupMember_Type",
                        column: x => x.PermitTypeDefId,
                        principalSchema: "cfg",
                        principalTable: "PermitTypeDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_PermitGroup",
                schema: "cfg",
                table: "PermitGroup",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PermitGroup_Registry",
                schema: "cfg",
                table: "PermitGroup",
                column: "RegistryDefId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermitGroupMember_Type",
                schema: "cfg",
                table: "PermitGroupMember",
                column: "PermitTypeDefId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermitGroupMember",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PermitGroup",
                schema: "cfg");
        }
    }
}
