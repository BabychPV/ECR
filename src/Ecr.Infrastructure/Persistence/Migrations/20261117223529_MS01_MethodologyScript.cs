using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MS01_MethodologyScript : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Engine",
                schema: "calc",
                table: "MethodologyVersion",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "ScriptHash",
                schema: "calc",
                table: "MethodologyVersion",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScriptText",
                schema: "calc",
                table: "MethodologyVersion",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MethodologyReadTable",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    TableCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyReadTable", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MRT_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyReadTable",
                schema: "calc",
                table: "MethodologyReadTable",
                columns: new[] { "MethodologyVersionId", "TableCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MethodologyReadTable",
                schema: "calc");

            migrationBuilder.DropColumn(
                name: "Engine",
                schema: "calc",
                table: "MethodologyVersion");

            migrationBuilder.DropColumn(
                name: "ScriptHash",
                schema: "calc",
                table: "MethodologyVersion");

            migrationBuilder.DropColumn(
                name: "ScriptText",
                schema: "calc",
                table: "MethodologyVersion");
        }
    }
}
