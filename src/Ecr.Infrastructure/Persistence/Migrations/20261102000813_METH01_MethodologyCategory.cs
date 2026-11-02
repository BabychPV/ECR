using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class METH01_MethodologyCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MethodologyCategory",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyCategory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MCat_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyCategory",
                schema: "calc",
                table: "MethodologyCategory",
                columns: new[] { "MethodologyVersionId", "Code" },
                unique: true);

            migrationBuilder.Sql(SeedCategoriesSql);
        }

        /// <summary>
        /// План CONST-CATEGORY: перелік кожної версії — різні наявні категорії її констант, як записано (регістр
        /// зберігається; варіантів, що різняться лише регістром, усередині версії немає — замір Land 2026-11-01).
        /// </summary>
        /// <remarks>
        /// ⚠ GROUP BY за зіставленням бази: два записи, що різняться лише регістром чи хвостовим пробілом, дають
        /// один рядок (MIN), а не порушення <c>UQ_MethodologyCategory</c>. Порядок — за кодом, після вже наявних.
        /// <c>NOT EXISTS</c> робить текст повторюваним (тест і повторне застосування не дублюють рядків).
        /// </remarks>
        public const string SeedCategoriesSql =
            """
            INSERT INTO calc.MethodologyCategory (MethodologyVersionId, Code, Description, Ordinal, IsRetired)
            SELECT g.MethodologyVersionId,
                   g.Code,
                   NULL,
                   ROW_NUMBER() OVER (PARTITION BY g.MethodologyVersionId ORDER BY g.Code)
                     + ISNULL((SELECT MAX(x.Ordinal) FROM calc.MethodologyCategory AS x
                                WHERE x.MethodologyVersionId = g.MethodologyVersionId), 0),
                   0
              FROM (SELECT c.MethodologyVersionId, MIN(c.Category) AS Code
                      FROM calc.MethodologyConstant AS c
                     WHERE c.Category IS NOT NULL AND LEN(c.Category) > 0
                     GROUP BY c.MethodologyVersionId, c.Category) AS g
             WHERE NOT EXISTS (SELECT 1 FROM calc.MethodologyCategory AS e
                                WHERE e.MethodologyVersionId = g.MethodologyVersionId AND e.Code = g.Code);
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MethodologyCategory",
                schema: "calc");
        }
    }
}
