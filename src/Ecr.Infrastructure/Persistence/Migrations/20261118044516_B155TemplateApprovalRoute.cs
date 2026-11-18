using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class B155TemplateApprovalRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.AddColumn<int>(
                name: "TemplateId",
                schema: "wf",
                table: "ApprovalRoute",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute",
                columns: new[] { "ProjectId", "TemplateId", "TemplateVersionId" });

            migrationBuilder.CreateIndex(
                name: "UQ_AR_Scope",
                schema: "wf",
                table: "ApprovalRoute",
                columns: new[] { "ProjectId", "TemplateId" },
                unique: true,
                filter: "[TemplateVersionId] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AR_Template",
                schema: "wf",
                table: "ApprovalRoute",
                column: "TemplateId",
                principalSchema: "cfg",
                principalTable: "Template",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AR_Template",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropIndex(
                name: "UQ_AR_Scope",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute",
                columns: new[] { "ProjectId", "TemplateVersionId" });
        }
    }
}
