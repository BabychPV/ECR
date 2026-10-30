using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RPTV01_ProjectVersionTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColumnRename",
                schema: "cfg",
                columns: table => new
                {
                    FromColumnDefId = table.Column<int>(type: "int", nullable: false),
                    ToColumnDefId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnRename", x => new { x.FromColumnDefId, x.ToColumnDefId });
                });

            migrationBuilder.CreateTable(
                name: "ProjectTemplateVersion",
                schema: "doc",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    FromPeriodKey = table.Column<int>(type: "int", nullable: false),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTemplateVersion", x => new { x.ProjectId, x.FromPeriodKey });
                    table.ForeignKey(
                        name: "FK_ProjectTemplateVersion_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectTemplateVersion_TemplateVersion",
                        column: x => x.TemplateVersionId,
                        principalSchema: "cfg",
                        principalTable: "TemplateVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnRename_To",
                schema: "cfg",
                table: "ColumnRename",
                column: "ToColumnDefId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion",
                column: "TemplateVersionId");

            // ✎ RPT-STABLE Part B: перший рядок шкали = поточна версія проєкту на ПЕРШОМУ періоді (ідемпотентно:
            // проєкт, що вже має рядки, і проєкт без періодів — пропускаються; порожня шкала читається як «чинна версія»).
            migrationBuilder.Sql("""
                INSERT doc.ProjectTemplateVersion (ProjectId, FromPeriodKey, TemplateVersionId)
                SELECT p.Id, MIN(pe.PeriodKey), p.TemplateVersionId
                FROM   doc.Project p
                JOIN   doc.Period pe ON pe.ProjectId = p.Id
                WHERE  NOT EXISTS (SELECT 1 FROM doc.ProjectTemplateVersion t WHERE t.ProjectId = p.Id)
                GROUP BY p.Id, p.TemplateVersionId;
                """);

            // SQL-відповідник ProjectVersionTimeline.VersionOf: найбільший FromPeriodKey ≤ ключа; раніше першого рядка —
            // перший рядок; шкали немає — Project.TemplateVersionId. Скалярна функція без побічних ефектів (інлайниться).
            migrationBuilder.Sql("""
                CREATE FUNCTION doc.fn_VersionOf (@ProjectId int, @PeriodKey int)
                RETURNS int
                AS
                BEGIN
                    RETURN COALESCE(
                        (SELECT TOP (1) t.TemplateVersionId FROM doc.ProjectTemplateVersion t
                         WHERE t.ProjectId = @ProjectId AND t.FromPeriodKey <= @PeriodKey ORDER BY t.FromPeriodKey DESC),
                        (SELECT TOP (1) t.TemplateVersionId FROM doc.ProjectTemplateVersion t
                         WHERE t.ProjectId = @ProjectId ORDER BY t.FromPeriodKey ASC),
                        (SELECT p.TemplateVersionId FROM doc.Project p WHERE p.Id = @ProjectId));
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS doc.fn_VersionOf;");

            migrationBuilder.DropTable(
                name: "ColumnRename",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "ProjectTemplateVersion",
                schema: "doc");
        }
    }
}
