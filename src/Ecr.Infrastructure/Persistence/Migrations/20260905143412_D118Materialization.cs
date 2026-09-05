using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D118Materialization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetRowKey",
                schema: "ext",
                table: "EntityFieldMap",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_EFM_Materialization",
                schema: "ext",
                table: "EntityFieldMap",
                sql: "TargetRowKey IS NULL OR TransformCode IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EFM_Transform",
                schema: "ext",
                table: "EntityFieldMap",
                sql: "TransformCode IS NULL OR TransformCode IN (N'Sum', N'Avg', N'Min', N'Max', N'Last', N'First')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EFM_Materialization",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EFM_Transform",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.DropColumn(
                name: "TargetRowKey",
                schema: "ext",
                table: "EntityFieldMap");
        }
    }
}
