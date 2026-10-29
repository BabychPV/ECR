using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CG01_ColumnHeaderGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HeaderGroupId",
                schema: "cfg",
                table: "ColumnDef",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ColumnHeaderGroupDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    ParentGroupId = table.Column<int>(type: "int", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HeaderL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnHeaderGroupDef", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ColumnHeaderGroupDef_Parent",
                        column: x => x.ParentGroupId,
                        principalSchema: "cfg",
                        principalTable: "ColumnHeaderGroupDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ColumnHeaderGroupDef_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnDef_HeaderGroupId",
                schema: "cfg",
                table: "ColumnDef",
                column: "HeaderGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ColumnHeaderGroupDef_ParentGroupId",
                schema: "cfg",
                table: "ColumnHeaderGroupDef",
                column: "ParentGroupId");

            migrationBuilder.CreateIndex(
                name: "UQ_ColumnHeaderGroupDef",
                schema: "cfg",
                table: "ColumnHeaderGroupDef",
                columns: new[] { "TableDefId", "Code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ColumnDef_HeaderGroup",
                schema: "cfg",
                table: "ColumnDef",
                column: "HeaderGroupId",
                principalSchema: "cfg",
                principalTable: "ColumnHeaderGroupDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ColumnDef_HeaderGroup",
                schema: "cfg",
                table: "ColumnDef");

            migrationBuilder.DropTable(
                name: "ColumnHeaderGroupDef",
                schema: "cfg");

            migrationBuilder.DropIndex(
                name: "IX_ColumnDef_HeaderGroupId",
                schema: "cfg",
                table: "ColumnDef");

            migrationBuilder.DropColumn(
                name: "HeaderGroupId",
                schema: "cfg",
                table: "ColumnDef");
        }
    }
}
