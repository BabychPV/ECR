using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ JQ-FIFO (b10; флейк CI 45157932160 <c>RecalculationYearPerPeriodCommitTests</c>): <c>itg.JobProgress.EnqueueSeq</c> —
    /// порядок вставки (послідовність <c>itg.JobProgressEnqueueSeq</c>, DEFAULT бази) як розв'язання нічиї <c>AvailableAt</c>
    /// (<c>datetime2(3)</c>) у claim черги; доти нічию розв'язував випадковий GUID у <c>JobId</c>, і FIFO двох постановок однієї
    /// мілісекунди не трималося.
    /// </summary>
    /// <remarks>
    /// ⚠ Стовпець nullable з DEFAULT без <c>WITH VALUES</c> — зміна лише метаданих: наявні рядки лишаються <c>NULL</c> (у
    /// <c>ORDER BY</c> вони йдуть першими — вони й старші). Індекси <c>IX_JobProgress_Claim</c> (ключ + <c>EnqueueSeq</c>, той
    /// самий порядок, що ORDER BY claim) і <c>IX_JobProgress_User_Active</c> (INCLUDE + <c>EnqueueSeq</c>) — фільтровані лише на
    /// живі рядки черги, тож перебудова мала.
    /// </remarks>
    public partial class JQFifoEnqueueSeq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobProgress_Claim",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropIndex(
                name: "IX_JobProgress_User_Active",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.CreateSequence(
                name: "JobProgressEnqueueSeq",
                schema: "itg");

            migrationBuilder.AddColumn<long>(
                name: "EnqueueSeq",
                schema: "itg",
                table: "JobProgress",
                type: "bigint",
                nullable: true,
                defaultValueSql: "NEXT VALUE FOR itg.JobProgressEnqueueSeq");

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_Claim",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "Lane", "State", "AvailableAt", "EnqueueSeq" },
                filter: "[Lane] IS NOT NULL AND [State] IN ('Queued', 'Running')")
                .Annotation("SqlServer:Include", new[] { "TargetKey", "LeaseUntil", "Attempt", "ReclaimCount" });

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_User_Active",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "Lane", "State", "CreatedByUserId" },
                filter: "[Lane] IS NOT NULL AND [State] IN ('Queued', 'Running')")
                .Annotation("SqlServer:Include", new[] { "AvailableAt", "FanOutParentJobId", "EnqueueSeq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobProgress_Claim",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropIndex(
                name: "IX_JobProgress_User_Active",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "EnqueueSeq",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropSequence(
                name: "JobProgressEnqueueSeq",
                schema: "itg");

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_Claim",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "Lane", "State", "AvailableAt" },
                filter: "[Lane] IS NOT NULL AND [State] IN ('Queued', 'Running')")
                .Annotation("SqlServer:Include", new[] { "TargetKey", "LeaseUntil", "Attempt", "ReclaimCount" });

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_User_Active",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "Lane", "State", "CreatedByUserId" },
                filter: "[Lane] IS NOT NULL AND [State] IN ('Queued', 'Running')")
                .Annotation("SqlServer:Include", new[] { "AvailableAt", "FanOutParentJobId" });
        }
    }
}
