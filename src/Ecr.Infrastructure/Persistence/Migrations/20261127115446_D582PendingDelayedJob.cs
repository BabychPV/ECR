using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ D-582: <c>itg.PendingDelayedJob</c> — довговічний слід відкладених постановок Quartz (автоперерахунок після правки,
    /// ланки джерел), щоб процес, що зник у вікні злиття, не забирав їх із собою (<c>QuartzDelayedJobRecovery</c>).
    /// </summary>
    /// <remarks>Нова порожня таблиця — даних міграція не чіпає; відкат (<c>Down</c>) — <c>DROP TABLE</c>.</remarks>
    public partial class D582PendingDelayedJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingDelayedJob",
                schema: "itg",
                columns: table => new
                {
                    JobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    JobCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingDelayedJob", x => x.JobId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingDelayedJob",
                schema: "itg");
        }
    }
}
