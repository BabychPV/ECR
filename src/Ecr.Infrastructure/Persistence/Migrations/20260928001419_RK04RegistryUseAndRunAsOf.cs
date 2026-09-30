using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RK04RegistryUseAndRunAsOf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RegistryAsOfUtc",
                schema: "calc",
                table: "CalculationRun",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RegistryUse",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKind = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    FormulaCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    FieldPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryUse", x => x.Id);
                    table.CheckConstraint("CK_RegUse_Kind", "[SourceKind] BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_RegUse_Def",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistryUse_Registry",
                schema: "cfg",
                table: "RegistryUse",
                column: "RegistryDefId")
                .Annotation("SqlServer:Include", new[] { "SourceKind", "SourceId", "FormulaCode", "FieldPath" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistryUse_Source",
                schema: "cfg",
                table: "RegistryUse",
                columns: new[] { "SourceKind", "SourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistryUse",
                schema: "cfg");

            migrationBuilder.DropColumn(
                name: "RegistryAsOfUtc",
                schema: "calc",
                table: "CalculationRun");
        }
    }
}
