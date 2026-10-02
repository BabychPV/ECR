using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FV1315ScheduleDependency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DependsOnScheduleId",
                schema: "ext",
                table: "CollectionSchedule",
                type: "int",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CS_DependsOn",
                schema: "ext",
                table: "CollectionSchedule",
                column: "DependsOnScheduleId",
                principalSchema: "ext",
                principalTable: "CollectionSchedule",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CS_DependsOn",
                schema: "ext",
                table: "CollectionSchedule");

            migrationBuilder.DropColumn(
                name: "DependsOnScheduleId",
                schema: "ext",
                table: "CollectionSchedule");
        }
    }
}
