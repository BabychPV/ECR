using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Атомарне захоплення рядка черги <c>itg.NotificationOutbox</c> (<c>Q-241</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Без цих двох колонок два одночасні відправники (`NotificationJob`
    /// щогодини і `CollectionJob.AlertAuthenticationAsync` негайно за
    /// `H-20`) читали ту саму партію `Pending`-рядків і надсилали той самий
    /// лист двічі. <c>ClaimedAt</c> — коли рядок узято в роботу (стан
    /// <c>Sending</c>), і за скільки часу вважати захоплення завислим і
    /// повернути в чергу. <c>ClaimToken</c> — однозначно ідентифікує партію,
    /// яку взяв САМЕ ЦЕЙ виклик <c>ClaimBatchAsync</c>.
    /// </remarks>
    public partial class Q241NotificationOutboxClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                schema: "itg",
                table: "NotificationOutbox",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                schema: "itg",
                table: "NotificationOutbox",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Claim",
                schema: "itg",
                table: "NotificationOutbox",
                columns: new[] { "State", "ClaimedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_Claim",
                schema: "itg",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                schema: "itg",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                schema: "itg",
                table: "NotificationOutbox");
        }
    }
}
