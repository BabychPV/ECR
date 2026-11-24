using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ P-1123-20 (огляд b11 №4): <c>calc.CalculationRun.SourceStateJson</c> — стан джерел (зв'язки Any з читанням із документа),
    /// які бачив прогін документа-споживача: відбиток рядків таблиць зв'язки і розрахункових F_ актуальних прогонів джерела
    /// на документ × період. Позначка «джерело змінено після затвердження» — і за змінами без aud.CellChange.
    /// </summary>
    /// <remarks>⚠ Дані не змінюються: nullable без DEFAULT — лише метадані. Прогони до міграції — NULL: позначка лише за журналом комірок, як доти.</remarks>
    public partial class P112320RunSourceState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceStateJson",
                schema: "calc",
                table: "CalculationRun",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceStateJson",
                schema: "calc",
                table: "CalculationRun");
        }
    }
}
