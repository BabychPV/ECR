using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class A2ApprovalRouteProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectId",
                schema: "wf",
                table: "ApprovalRoute",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute",
                columns: new[] { "ProjectId", "TemplateVersionId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AR_Project",
                schema: "wf",
                table: "ApprovalRoute",
                column: "ProjectId",
                principalSchema: "doc",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AR_Project",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalRoute_Scope",
                schema: "wf",
                table: "ApprovalRoute");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                schema: "wf",
                table: "ApprovalRoute");
        }
    }
}
