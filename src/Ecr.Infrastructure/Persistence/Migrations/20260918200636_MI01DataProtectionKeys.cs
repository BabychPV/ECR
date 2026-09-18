using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// `MI-01`: спільне кільце ключів DataProtection у базі.
    /// </summary>
    /// <remarks>
    /// Форму таблиці диктує `Microsoft.AspNetCore.DataProtection.
    /// EntityFrameworkCore` — читає її власний провайдер пакета. Наше тут одне
    /// рішення: схема `sec`, а не конвенційна `dbo`. Причина не в охайності —
    /// у таблиці лежать ключі, якими підписана сесія, і в режимі HTTP без
    /// сертифіката вони лежать ВІДКРИТО. Єдиний захист тоді — права в базі,
    /// а `DENY` видають на таблицю, названу в контракті схеми
    /// (`docs/build/02a-db-schema.md` §11).
    /// </remarks>
    public partial class MI01DataProtectionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKey",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKey", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKey",
                schema: "sec");
        }
    }
}
