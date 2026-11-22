using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ D-568: <c>calc.MethodologyReadField</c> — поля ВЛАСНОЇ таблиці, які читає скрипт версії методології (і чи читає
    /// місячне поле за інший місяць). Пише публікація (<c>ScriptMaterializer</c>); версії, опубліковані до міграції, дозаписує
    /// старт застосунку (<c>MethodologyReadFieldsBackfill</c>), а доти прохід читача вважає їх читачами всього.
    /// </summary>
    /// <remarks>⚠ Лише нова порожня таблиця й індекс — наявні дані не змінюються.</remarks>
    public partial class D568MethodologyReadField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MethodologyReadField",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    FieldCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OtherMonths = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyReadField", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MRF_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyReadField",
                schema: "calc",
                table: "MethodologyReadField",
                columns: new[] { "MethodologyVersionId", "FieldCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MethodologyReadField",
                schema: "calc");
        }
    }
}
