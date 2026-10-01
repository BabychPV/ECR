using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D256SmtpSettingsAndChannelRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationChannelRole",
                schema: "sys_ecr",
                columns: table => new
                {
                    ChannelId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannelRole", x => new { x.ChannelId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_NotificationChannelRole_Channel",
                        column: x => x.ChannelId,
                        principalSchema: "sys_ecr",
                        principalTable: "NotificationChannel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationChannelRole_Role",
                        column: x => x.RoleId,
                        principalSchema: "sec",
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SmtpSettings",
                schema: "sys_ecr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    EncryptionMode = table.Column<byte>(type: "tinyint", nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AuthMode = table.Column<byte>(type: "tinyint", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    PasswordProtected = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmtpSettings", x => x.Id);
                    table.CheckConstraint("CK_SmtpSettings_Auth", "AuthMode IN (0, 1)");
                    table.CheckConstraint("CK_SmtpSettings_Encryption", "EncryptionMode IN (0, 1)");
                    table.CheckConstraint("CK_SmtpSettings_Port", "Port BETWEEN 1 AND 65535");
                    table.CheckConstraint("CK_SmtpSettings_Singleton", "Id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannelRole_Role",
                schema: "sys_ecr",
                table: "NotificationChannelRole",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationChannelRole",
                schema: "sys_ecr");

            migrationBuilder.DropTable(
                name: "SmtpSettings",
                schema: "sys_ecr");
        }
    }
}
