using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Таблиця тестів методології (<c>calc.TestCase</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Закриває <c>P-08</c>: ФВ-13.7 прямо посилалася на цю таблицю, а в
    /// схемі її не було. Доки її не існувало, порт віддавав порожній набір, і
    /// публікація методології відхилялася <b>завжди</b> — правило працювало,
    /// але опублікувати не можна було нічого.
    /// </remarks>
    public partial class Audit7TestCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TestCase",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpectedJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Tolerance = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false, defaultValue: 0m)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestCase", x => x.Id);
                    table.CheckConstraint("CK_TC_Tolerance", "Tolerance >= 0");
                    table.ForeignKey(
                        name: "FK_TC_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_TestCase",
                schema: "calc",
                table: "TestCase",
                columns: new[] { "MethodologyVersionId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TestCase",
                schema: "calc");
        }
    }
}
