using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class S1RecalculationApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecalculationApproval",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    PeriodState = table.Column<byte>(type: "tinyint", nullable: false),
                    PeriodStateChangedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ConfirmedByUserId = table.Column<int>(type: "int", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UsedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecalculationApproval", x => x.Id);
                    table.CheckConstraint("CK_RecalcApproval_FourEyes", "ConfirmedByUserId IS NULL OR ConfirmedByUserId <> RequestedByUserId");
                    table.CheckConstraint("CK_RecalcApproval_UsedConfirmed", "UsedAt IS NULL OR ConfirmedByUserId IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_RecalcApproval_ConfirmedBy",
                        column: x => x.ConfirmedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecalcApproval_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecalcApproval_RequestedBy",
                        column: x => x.RequestedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecalcApproval_Project",
                schema: "calc",
                table: "RecalculationApproval",
                columns: new[] { "ProjectId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecalculationApproval",
                schema: "calc");
        }
    }
}
