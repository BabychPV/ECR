using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HSE301M1TimeWeighted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EFM_Transform",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.AddColumn<bool>(
                name: "IsStep",
                schema: "ext",
                table: "EntityFieldMap",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_EFM_Transform",
                schema: "ext",
                table: "EntityFieldMap",
                sql: "TransformCode IS NULL OR TransformCode IN (N'Sum', N'Avg', N'Min', N'Max', N'Last', N'First', N'TimeWeightedAvg', N'TimeIntegral')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EFM_Transform",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.DropColumn(
                name: "IsStep",
                schema: "ext",
                table: "EntityFieldMap");

            // ⚠ Мапінги з TimeWeightedAvg/TimeIntegral відкат зупинять на цьому
            // обмеженні — навмисно: переписати їм спосіб згортання «на щось
            // схоже» означало б мовчки змінити числа в комірках.
            migrationBuilder.AddCheckConstraint(
                name: "CK_EFM_Transform",
                schema: "ext",
                table: "EntityFieldMap",
                sql: "TransformCode IS NULL OR TransformCode IN (N'Sum', N'Avg', N'Min', N'Max', N'Last', N'First')");
        }
    }
}
