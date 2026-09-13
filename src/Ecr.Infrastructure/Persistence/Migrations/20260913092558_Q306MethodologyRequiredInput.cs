using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Q306MethodologyRequiredInput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MethodologyRequiredInput",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    HintL10n = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyRequiredInput", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MRI_Column",
                        column: x => x.ColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MRI_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyRequiredInput",
                schema: "calc",
                table: "MethodologyRequiredInput",
                columns: new[] { "MethodologyVersionId", "ColumnDefId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MethodologyRequiredInput",
                schema: "calc");
        }
    }
}
