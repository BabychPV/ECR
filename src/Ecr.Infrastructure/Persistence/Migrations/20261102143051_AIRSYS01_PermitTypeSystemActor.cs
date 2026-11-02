using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AIRSYS01_PermitTypeSystemActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PermitType_Published",
                schema: "cfg",
                table: "PermitTypeDef");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermitType_Published",
                schema: "cfg",
                table: "PermitTypeDef",
                sql: "Status <> 1 OR PublishedAt IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PermitType_Published",
                schema: "cfg",
                table: "PermitTypeDef");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PermitType_Published",
                schema: "cfg",
                table: "PermitTypeDef",
                sql: "Status <> 1 OR (PublishedAt IS NOT NULL AND PublishedByUserId IS NOT NULL)");
        }
    }
}
