using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// B127: перейменування колонок (<c>cfg.ColumnRename</c>) прив'язуються до проєкту, перенос якого їх застосував
    /// (<c>cfg.ColumnRenameProject</c>); родовід документа бере лише пари свого проєкту.
    /// </summary>
    /// <remarks>
    /// Наявні рядки <c>cfg.ColumnRename</c> отримують <c>IsLegacyGlobal = 1</c>: журналу переносів із переліком пар немає
    /// (подія безпеки <c>DocumentVersionMigrated</c> несе лише кількість, часова шкала проєкту після переносу всього проєкту
    /// лишає один рядок), тож проєкт не відновити — такі пари й далі діють для всіх проєктів, як до B127. Ризик лишається
    /// лише для пар, записаних до цієї міграції. Нові рядки — <c>0</c>, діють через прив'язку. Ідемпотентність оновлення —
    /// скрипт <c>migrations script --idempotent</c>; UPDATE — динамічним SQL, щоб пакет компілювався незалежно від
    /// подальших змін схеми.
    /// </remarks>
    public partial class B127_ColumnRenameProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLegacyGlobal",
                schema: "cfg",
                table: "ColumnRename",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("EXEC(N'UPDATE cfg.ColumnRename SET IsLegacyGlobal = 1 WHERE IsLegacyGlobal = 0;');");

            migrationBuilder.CreateTable(
                name: "ColumnRenameProject",
                schema: "cfg",
                columns: table => new
                {
                    FromColumnDefId = table.Column<int>(type: "int", nullable: false),
                    ToColumnDefId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnRenameProject", x => new { x.FromColumnDefId, x.ToColumnDefId, x.ProjectId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnRenameProject",
                schema: "cfg");

            migrationBuilder.DropColumn(
                name: "IsLegacyGlobal",
                schema: "cfg",
                table: "ColumnRename");
        }
    }
}
