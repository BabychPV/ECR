using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Q206DropScriptVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScriptVersion",
                schema: "calc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScriptVersion",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompiledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CompilerDiagnostics = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    HasGreenTest = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(max)", nullable: false)
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
                name: "UQ_ScriptVersion",
                schema: "calc",
                table: "ScriptVersion",
                column: "MethodologyVersionId",
                unique: true);
        }
    }
}
