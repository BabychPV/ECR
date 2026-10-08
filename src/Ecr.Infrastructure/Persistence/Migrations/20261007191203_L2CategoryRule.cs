using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class L2CategoryRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CategoryRule",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    Expression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CR_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_CategoryRule_Version",
                schema: "calc",
                table: "CategoryRule",
                column: "MethodologyVersionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryRule",
                schema: "calc");
        }
    }
}
