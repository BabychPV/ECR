using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Биття серця фонової задачі в <c>itg.JobProgress</c> (<c>Q-331</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Без цієї колонки прибирання на старті (<c>FailStaleAsync</c>) не мало
    /// ЖОДНОГО предиката застарілості й валило кожен рядок у стані
    /// <c>Running</c>/<c>Queued</c>. Інстансів у розгортанні кілька (ціль —
    /// 100 одночасних користувачів), тож перезапуск інстанса B позначав
    /// <c>Failed</c> перерахунки, експорти й імпорти, які в цю саму мить
    /// виконував інстанс A: користувач бачив провал задачі, яка насправді
    /// доробила успішно. <c>HeartbeatAt</c> — коли процес-власник востаннє
    /// підтвердив, що задача жива; мертвий процес не пише нічого, і тільки це
    /// відрізняє покинуту задачу від чужої живої.
    ///
    /// ⚠ Колонка NULLABLE і БЕЗ бекфілу навмисно. Наявні рядки
    /// <c>Running</c>/<c>Queued</c> належать процесу, який зупинявся заради
    /// розгортання цієї ж міграції, тож він гарантовано мертвий. <c>NULL</c>
    /// читається як «биття не було ніколи» = покинута, і перше ж прибирання
    /// закриває їх — саме так, як робила стара поведінка, і саме для цих
    /// рядків вона була правильною.
    ///
    /// ⚠ Індекс — з тієї ж причини, що й <c>IX_Outbox_Claim</c> (<c>Q-241</c>):
    /// прибирання фільтрує саме за парою (State, HeartbeatAt), а історія задач
    /// не видаляється й росте.
    /// </remarks>
    public partial class Q331JobProgressHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HeartbeatAt",
                schema: "itg",
                table: "JobProgress",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_Stale",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "State", "HeartbeatAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobProgress_Stale",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                schema: "itg",
                table: "JobProgress");
        }
    }
}
