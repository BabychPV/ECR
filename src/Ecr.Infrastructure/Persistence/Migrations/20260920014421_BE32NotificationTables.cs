using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Сповіщення, що налаштовуються в застосунку (<c>BE-32</c>): канали й
    /// правила в <c>sys_ecr</c>, журнал доставок в <c>itg</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>EnsureSchema("sys_ecr")</c> тут уперше: досі схему створював лише
    /// <c>08-system-tables.sql</c>, який іде ПІСЛЯ міграцій. Обидва — під
    /// <c>IF SCHEMA_ID(...) IS NULL</c>, тож порядок не ламається.
    ///
    /// ⚠ Усі три таблиці починаються порожніми: без каналів поведінка лишається
    /// колишньою (SMTP із конфігурації процесу).
    /// </remarks>
    public partial class BE32NotificationTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sys_ecr");

            migrationBuilder.CreateTable(
                name: "NotificationChannel",
                schema: "sys_ecr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SecretProtected = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ModifiedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannel", x => x.Id);
                    table.CheckConstraint("CK_NotificationChannel_Kind", "Kind IN (1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "NotificationDelivery",
                schema: "itg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ChannelId = table.Column<int>(type: "int", nullable: false),
                    EventKind = table.Column<byte>(type: "tinyint", nullable: false),
                    EventKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDelivery", x => x.Id);
                    table.CheckConstraint("CK_NotificationDelivery_Status", "Status IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "NotificationRule",
                schema: "sys_ecr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventKind = table.Column<byte>(type: "tinyint", nullable: false),
                    ChannelId = table.Column<int>(type: "int", nullable: false),
                    MinSeverity = table.Column<byte>(type: "tinyint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRule_Channel",
                        column: x => x.ChannelId,
                        principalSchema: "sys_ecr",
                        principalTable: "NotificationChannel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_NotificationChannel_Name",
                schema: "sys_ecr",
                table: "NotificationChannel",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_Dedup",
                schema: "itg",
                table: "NotificationDelivery",
                columns: new[] { "ChannelId", "EventKey", "At" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRule_Channel",
                schema: "sys_ecr",
                table: "NotificationRule",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "UQ_NotificationRule_EventChannel",
                schema: "sys_ecr",
                table: "NotificationRule",
                columns: new[] { "EventKind", "ChannelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDelivery",
                schema: "itg");

            migrationBuilder.DropTable(
                name: "NotificationRule",
                schema: "sys_ecr");

            migrationBuilder.DropTable(
                name: "NotificationChannel",
                schema: "sys_ecr");
        }
    }
}
