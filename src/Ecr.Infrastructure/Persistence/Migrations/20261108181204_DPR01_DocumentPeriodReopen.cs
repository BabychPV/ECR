using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DPR01_DocumentPeriodReopen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentPeriodReopen",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    OpenUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    OpenedByUserId = table.Column<int>(type: "int", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedByUserId = table.Column<int>(type: "int", nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPeriodReopen", x => x.Id);
                    table.CheckConstraint("CK_DocumentPeriodReopen_Close", "(ClosedAt IS NULL AND CloseReason IS NULL AND ClosedByUserId IS NULL) OR (ClosedAt IS NOT NULL AND CloseReason IS NOT NULL)");
                    table.CheckConstraint("CK_DocumentPeriodReopen_Period", "PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 1 AND 99");
                    table.CheckConstraint("CK_DocumentPeriodReopen_Reason", "LEN(Reason) > 0");
                    table.ForeignKey(
                        name: "FK_DocumentPeriodReopen_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPeriodReopen_OpenedBy",
                        column: x => x.OpenedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPeriodReopen_OpenUntil",
                schema: "doc",
                table: "DocumentPeriodReopen",
                column: "OpenUntil",
                filter: "[ClosedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_DocumentPeriodReopen_Active",
                schema: "doc",
                table: "DocumentPeriodReopen",
                columns: new[] { "DocumentId", "PeriodKey" },
                unique: true,
                filter: "[ClosedAt] IS NULL")
                .Annotation("SqlServer:Include", new[] { "OpenUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentPeriodReopen",
                schema: "doc");
        }
    }
}
