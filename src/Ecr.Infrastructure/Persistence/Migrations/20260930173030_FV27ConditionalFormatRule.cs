using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FV27ConditionalFormatRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConditionalFormatRule",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false),
                    ColumnCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Operator = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ValueTo = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BackgroundHex = table.Column<string>(type: "varchar(7)", unicode: false, maxLength: 7, nullable: true),
                    ForegroundHex = table.Column<string>(type: "varchar(7)", unicode: false, maxLength: 7, nullable: true),
                    IsBold = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_CondFmt_Bold")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConditionalFormatRule", x => x.Id);
                    table.CheckConstraint("CK_CondFmt_Operator", "Operator IN (N'gt', N'ge', N'lt', N'le', N'eq', N'ne', N'between', N'empty', N'notEmpty')");
                    table.ForeignKey(
                        name: "FK_CondFmt_TV",
                        column: x => x.TemplateVersionId,
                        principalSchema: "cfg",
                        principalTable: "TemplateVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_CondFmt_Order",
                schema: "cfg",
                table: "ConditionalFormatRule",
                columns: new[] { "TemplateVersionId", "ColumnCode", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConditionalFormatRule",
                schema: "cfg");
        }
    }
}
