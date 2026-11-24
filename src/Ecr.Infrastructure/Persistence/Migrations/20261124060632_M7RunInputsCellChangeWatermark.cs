using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ W15z M7 (огляд b11 №3): <c>calc.CalculationRun.InputsCellChangeWatermark</c> — закомічена межа журналу комірок на старті
    /// прогону документа (входи чисел; не цілком перерахований період — min з межею попереднього актуального прогону). Позначка
    /// «джерело змінено після затвердження» знімається, лише коли Id правки не вищий за межу чинного прогону — без годинників.
    /// </summary>
    /// <remarks>
    /// ⚠ Дані не змінюються: стовпець nullable без DEFAULT — зміна лише метаданих. Прогони до міграції лишаються з <c>NULL</c> —
    /// позначка не знімається (обережно), доки документ не перерахують.
    /// </remarks>
    public partial class M7RunInputsCellChangeWatermark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InputsCellChangeWatermark",
                schema: "calc",
                table: "CalculationRun",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputsCellChangeWatermark",
                schema: "calc",
                table: "CalculationRun");
        }
    }
}
