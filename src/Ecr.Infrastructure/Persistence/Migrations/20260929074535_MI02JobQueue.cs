using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Черга фонових задач у базі (<c>MI-02</c>, <c>D14-01</c>, <c>D-208</c>):
    /// колонки черги в <c>itg.JobProgress</c>, <c>CK_JobProgress_QueueShape</c>,
    /// <c>UX_JobProgress_Target_Queued</c>/<c>_Running</c>, <c>IX_JobProgress_Claim</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Усі колонки nullable, без заповнення: наявні рядки — дзеркало Quartz
    /// (<c>Lane IS NULL</c>), CHECK їх пропускає, фільтровані індекси їх не
    /// бачать (<c>TargetKey</c> і <c>Lane</c> у них <c>NULL</c>).
    ///
    /// ⚠ Фільтровані індекси вимагають <c>QUOTED_IDENTIFIER ON</c> /
    /// <c>ANSI_NULLS ON</c> при створенні і при кожному записі в таблицю.
    /// Скрипт міграції виконується <c>sqlcmd -I</c> (<c>setup-dev-db.ps1</c>,
    /// <c>verify-sql-scripts.ps1</c>) — той самий прийом, що й для
    /// <c>UX_CalculationRun_Current</c> (<c>Q330CalculationRunCurrentUnique</c>).
    /// </remarks>
    public partial class MI02JobQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTime>(
                name: "AvailableAt",
                schema: "itg",
                table: "JobProgress",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelRequestedAt",
                schema: "itg",
                table: "JobProgress",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                schema: "itg",
                table: "JobProgress",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lane",
                schema: "itg",
                table: "JobProgress",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                schema: "itg",
                table: "JobProgress",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                schema: "itg",
                table: "JobProgress",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReclaimCount",
                schema: "itg",
                table: "JobProgress",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetKey",
                schema: "itg",
                table: "JobProgress",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobProgress_Claim",
                schema: "itg",
                table: "JobProgress",
                columns: new[] { "Lane", "State", "AvailableAt" },
                filter: "[Lane] IS NOT NULL AND [State] IN ('Queued', 'Running')")
                .Annotation("SqlServer:Include", new[] { "TargetKey", "LeaseUntil", "Attempt", "ReclaimCount" });

            migrationBuilder.CreateIndex(
                name: "UX_JobProgress_Target_Queued",
                schema: "itg",
                table: "JobProgress",
                column: "TargetKey",
                unique: true,
                filter: "[State] = 'Queued' AND [TargetKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_JobProgress_Target_Running",
                schema: "itg",
                table: "JobProgress",
                column: "TargetKey",
                unique: true,
                filter: "[State] = 'Running' AND [TargetKey] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobProgress_QueueShape",
                schema: "itg",
                table: "JobProgress",
                sql: "[Lane] IS NULL OR [AvailableAt] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_JobProgress_Claim",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropIndex(
                name: "UX_JobProgress_Target_Queued",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropIndex(
                name: "UX_JobProgress_Target_Running",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobProgress_QueueShape",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "CancelRequestedAt",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "Lane",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "Payload",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "ReclaimCount",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "TargetKey",
                schema: "itg",
                table: "JobProgress");
        }
    }
}
