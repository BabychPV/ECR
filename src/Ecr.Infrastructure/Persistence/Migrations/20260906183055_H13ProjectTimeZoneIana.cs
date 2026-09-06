using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// `doc.Project.TimeZoneId` — ідентифікатор IANA, а не Windows
    /// (директива ПК-1 №06 §3, крок `H-13`).
    /// </summary>
    /// <remarks>
    /// Колонка була `NOT NULL` із DEFAULT `N'Central Asia Standard Time'` —
    /// Windows-ідентифікатором. Міграція робить три речі: переводить наявні
    /// рядки в IANA, прибирає DEFAULT і ставить грубу сітку проти повернення
    /// Windows-ідентифікаторів і зсувів.
    /// </remarks>
    public partial class H13ProjectTimeZoneIana : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⛔ Значення для наявних рядків — `Asia/Almaty`, і причина не
            // «десь у Казахстані», а точна: це те, у що сама платформа
            // переводить старе значення. Виміряно на цій машині
            // (.NET 10.0.11): `TryConvertWindowsIdToIanaId
            // ('Central Asia Standard Time') = 'Asia/Almaty'`, і обидва дають
            // той самий базовий зсув +06:00.
            //
            // ⛔ Саме тому НЕ `Asia/Aqtau`, хоч майданчики й на заході країни.
            // `Asia/Aqtau` — це +05:00, тобто кожна вже порахована межа
            // (`ComputedOpenAt`, `ComputedGraceAt`, `ComputedCloseAt`)
            // зсунулася б на ГОДИНУ, а разом із нею — межа «пізньої зміни» на
            // вже поданих формах. Міграція не має права переписувати минуле;
            // проєктам, яким потрібен інший пояс, його ставить людина,
            // допоки перший період не відкрито (ФВ-1.1a).
            //
            // ⚠ WHERE за точним значенням, а не за «усе, що з пробілом»:
            // єдиним джерелом Windows-ідентифікаторів у цій колонці був
            // `DF_Project_Tz`, який прибирає наступний крок, і підставляв він
            // рівно цей рядок. Якщо в базі виявиться інший — обмеження
            // `CK_Project_TzIana` не створиться, і міграція зупиниться з
            // іменем обмеження. Це навмисно: тихо підмінити невідомий пояс
            // означало б зсунути межі періодів невідомо куди.
            migrationBuilder.Sql(
                "UPDATE doc.Project SET TimeZoneId = N'Asia/Almaty' "
                + "WHERE TimeZoneId = N'Central Asia Standard Time';");

            migrationBuilder.AlterColumn<string>(
                name: "TimeZoneId",
                schema: "doc",
                table: "Project",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldDefaultValue: "Central Asia Standard Time")
                .OldAnnotation("Relational:DefaultConstraintName", "DF_Project_Tz");

            // ⚠ Груба сітка, а не повна перевірка IANA: точне правило живе в
            // `SiteTimeZone` (перевіряє `TryConvertIanaIdToWindowsId`). База —
            // остання лінія і ловить рівно ті два класи значень, які в цю
            // колонку справді потрапляли: Windows-ідентифікатори
            // (`Central Asia Standard Time` — пробіли) і зсуви (`+05:00`,
            // `UTC+13` — `+` або `:`). Жоден із 139 ідентифікаторів IANA цієї
            // машини не містить ні пробілу, ні `+`, ні `:` (виміряно).
            migrationBuilder.AddCheckConstraint(
                name: "CK_Project_TzIana",
                schema: "doc",
                table: "Project",
                sql: "LEN(TimeZoneId) > 0 AND TimeZoneId NOT LIKE N'%[ +:]%'");
        }

        /// <inheritdoc />
        /// <remarks>
        /// ⛔ Дані НЕ повертаються назад у `Central Asia Standard Time`, і це
        /// не забутий рядок. Після цієї міграції `Asia/Almaty` мають і ті
        /// проєкти, які його обрали людиною; зворотний `UPDATE` не відрізнив
        /// би їх від переведених і переписав би чужий свідомий вибір
        /// Windows-ідентифікатором. Відкат схеми повертає можливість писати
        /// старі значення — не сам факт, що їх колись писали.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Project_TzIana",
                schema: "doc",
                table: "Project");

            migrationBuilder.AlterColumn<string>(
                name: "TimeZoneId",
                schema: "doc",
                table: "Project",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Central Asia Standard Time",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64)
                .Annotation("Relational:DefaultConstraintName", "DF_Project_Tz");
        }
    }
}
