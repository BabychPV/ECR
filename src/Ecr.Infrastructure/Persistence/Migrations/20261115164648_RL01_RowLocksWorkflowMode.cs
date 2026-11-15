using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RL01_RowLocksWorkflowMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "WorkflowMode",
                schema: "cfg",
                table: "TemplateVersion",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0)
                .Annotation("Relational:DefaultConstraintName", "DF_TV_WorkflowMode");

            migrationBuilder.CreateTable(
                name: "RowLock",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    SheetCopyId = table.Column<long>(type: "bigint", nullable: true),
                    TableCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LockFrom = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LockUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    OwnerUserId = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReleasedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RowLock", x => x.Id);
                    table.CheckConstraint("CK_RowLock_Period", "PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 0 AND 99");
                    table.CheckConstraint("CK_RowLock_Range", "LockUntil > LockFrom");
                    table.CheckConstraint("CK_RowLock_Release", "(ReleasedAt IS NULL AND ReleasedByUserId IS NULL) OR (ReleasedAt IS NOT NULL AND ReleasedByUserId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_RowLock_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RowLock_Owner",
                        column: x => x.OwnerUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RowLock_Doc_Period",
                schema: "doc",
                table: "RowLock",
                columns: new[] { "DocumentId", "PeriodKey" },
                filter: "[ReleasedAt] IS NULL")
                .Annotation("SqlServer:Include", new[] { "TableCode", "RowKey", "LockFrom", "LockUntil", "OwnerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RowLock",
                schema: "doc");

            migrationBuilder.DropColumn(
                name: "WorkflowMode",
                schema: "cfg",
                table: "TemplateVersion")
                .Annotation("Relational:DefaultConstraintName", "DF_TV_WorkflowMode");
        }
    }
}
