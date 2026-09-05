using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Виведення версії шаблону з обігу (<c>ФВ-7.8</c>): хто і коли.
    /// </summary>
    /// <remarks>
    /// ⛔ Стан <c>Deprecated</c> існував від Етапу 1 і був <b>недосяжним</b>:
    /// <c>IsStructurallyFrozen</c> його враховував, текст відмови публікації на
    /// нього посилався, а перевести версію в цей стан не міг ніхто. Тобто
    /// відкат опублікованої версії був неможливий у принципі, і єдиним
    /// «відкатом» лишалося видалення — рівно те, що вимога забороняє.
    ///
    /// ⚠ Обидві колонки <c>NULL</c>: версії в обігу їх не мають, і значення за
    /// замовчуванням тут означало б, що кожна версія колись була виведена.
    /// </remarks>
    [DbContext(typeof(EcrDbContext))]
    [Migration("20260906120000_A746Deprecation")]
    public partial class A746Deprecation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeprecatedAt",
                schema: "cfg",
                table: "TemplateVersion",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeprecatedByUserId",
                schema: "cfg",
                table: "TemplateVersion",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "DeprecatedAt",
                schema: "cfg",
                table: "TemplateVersion");

            migrationBuilder.DropColumn(
                name: "DeprecatedByUserId",
                schema: "cfg",
                table: "TemplateVersion");
        }
    }
}
