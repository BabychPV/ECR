using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PT01_ProjectTemplates : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// ✎ B74 P1 + Land-year L0 (<c>docs/build/B74-L0-PT01-migration-design.md</c>). Згенероване EF поправлено руками:
        /// відмова на рядку шкали чужого шаблону ПЕРШОЮ; <c>ProjectTemplateVersion.TemplateId</c> додається NULL, заповнюється
        /// бекфілом 1:1 і лише тоді стає NOT NULL і входить у PK (EF ставив <c>defaultValue: 0</c>, і ключ (P, 0, F) лягав би
        /// на всі рядки); функції — останніми, коли колонки вже є. Тексти SQL — <see cref="PT01ProjectTemplatesSql"/>.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PT01ProjectTemplatesSql.Precheck);

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProjectTemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.AddColumn<int>(
                name: "TemplateId",
                schema: "doc",
                table: "ProjectTemplateVersion",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TemplateId",
                schema: "doc",
                table: "Document",
                type: "int",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TemplateVersion_Id_Template",
                schema: "cfg",
                table: "TemplateVersion",
                columns: new[] { "Id", "TemplateId" });

            migrationBuilder.CreateTable(
                name: "MonthInputAlias",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MonthNumber = table.Column<byte>(type: "tinyint", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthInputAlias", x => x.Id);
                    table.CheckConstraint("CK_MonthInputAlias_Month", "MonthNumber BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_MonthInputAlias_Column",
                        column: x => x.ColumnDefId,
                        principalSchema: "cfg",
                        principalTable: "ColumnDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MonthInputAlias_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTemplate",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    TemplateId = table.Column<int>(type: "int", nullable: false),
                    CurrentTemplateVersionId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTemplate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTemplate_Project",
                        column: x => x.ProjectId,
                        principalSchema: "doc",
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectTemplate_Template",
                        column: x => x.TemplateId,
                        principalSchema: "cfg",
                        principalTable: "Template",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectTemplate_TemplateVersion",
                        columns: x => new { x.CurrentTemplateVersionId, x.TemplateId },
                        principalSchema: "cfg",
                        principalTable: "TemplateVersion",
                        principalColumns: new[] { "Id", "TemplateId" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Бекфіл 1:1 з Project.TemplateVersionId (повторюваний), лише потім NOT NULL і новий PK.
            migrationBuilder.Sql(PT01ProjectTemplatesSql.Backfill);

            migrationBuilder.AlterColumn<int>(
                name: "TemplateId",
                schema: "doc",
                table: "ProjectTemplateVersion",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProjectTemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion",
                columns: new[] { "ProjectId", "TemplateId", "FromPeriodKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplateVersion_Template",
                schema: "doc",
                table: "ProjectTemplateVersion",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion",
                columns: new[] { "TemplateVersionId", "TemplateId" });

            migrationBuilder.CreateIndex(
                name: "IX_Document_Template",
                schema: "doc",
                table: "Document",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_MonthInputAlias_Column",
                schema: "cfg",
                table: "MonthInputAlias",
                column: "ColumnDefId");

            migrationBuilder.CreateIndex(
                name: "UQ_MonthInputAlias",
                schema: "cfg",
                table: "MonthInputAlias",
                columns: new[] { "TableDefId", "Alias", "MonthNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplate_CurrentVersion",
                schema: "doc",
                table: "ProjectTemplate",
                columns: new[] { "CurrentTemplateVersionId", "TemplateId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplate_Template",
                schema: "doc",
                table: "ProjectTemplate",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "UQ_ProjectTemplate",
                schema: "doc",
                table: "ProjectTemplate",
                columns: new[] { "ProjectId", "TemplateId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Document_Template",
                schema: "doc",
                table: "Document",
                column: "TemplateId",
                principalSchema: "cfg",
                principalTable: "Template",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTemplateVersion_Template",
                schema: "doc",
                table: "ProjectTemplateVersion",
                column: "TemplateId",
                principalSchema: "cfg",
                principalTable: "Template",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion",
                columns: new[] { "TemplateVersionId", "TemplateId" },
                principalSchema: "cfg",
                principalTable: "TemplateVersion",
                principalColumns: new[] { "Id", "TemplateId" },
                onDelete: ReferentialAction.Restrict);

            // Функції — останніми: fn_TemplateVersionOf читає нову колонку шкали; fn_VersionOf — шлях сумісності через неї.
            migrationBuilder.Sql(PT01ProjectTemplatesSql.CreateTemplateVersionOf);
            migrationBuilder.Sql(PT01ProjectTemplatesSql.AlterVersionOfCompat);
        }

        /// <inheritdoc />
        /// <remarks>
        /// ⛔ Відмова (51075), якщо проєкт уже має кілька шаблонів: відкат втратив би дані. Далі — тіло fn_VersionOf з RPTV01
        /// (до зняття колонок, від яких залежить нова функція) і лише тоді згенероване EF.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PT01ProjectTemplatesSql.DownGuard);
            migrationBuilder.Sql(PT01ProjectTemplatesSql.RestoreVersionOf);
            migrationBuilder.Sql(PT01ProjectTemplatesSql.DropTemplateVersionOf);

            migrationBuilder.DropForeignKey(
                name: "FK_Document_Template",
                schema: "doc",
                table: "Document");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTemplateVersion_Template",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropTable(
                name: "MonthInputAlias",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "ProjectTemplate",
                schema: "doc");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TemplateVersion_Id_Template",
                schema: "cfg",
                table: "TemplateVersion");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProjectTemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTemplateVersion_Template",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropIndex(
                name: "IX_Document_Template",
                schema: "doc",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                schema: "doc",
                table: "ProjectTemplateVersion");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                schema: "doc",
                table: "Document");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProjectTemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion",
                columns: new[] { "ProjectId", "FromPeriodKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion",
                column: "TemplateVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTemplateVersion_TemplateVersion",
                schema: "doc",
                table: "ProjectTemplateVersion",
                column: "TemplateVersionId",
                principalSchema: "cfg",
                principalTable: "TemplateVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
