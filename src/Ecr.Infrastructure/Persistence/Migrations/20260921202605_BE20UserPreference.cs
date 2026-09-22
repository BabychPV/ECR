using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BE20UserPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserPreference",
                schema: "sec",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    ValueJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreference", x => new { x.UserId, x.Key });
                    table.ForeignKey(
                        name: "FK_UserPreference_User",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserPreference",
                schema: "sec");
        }
    }
}
