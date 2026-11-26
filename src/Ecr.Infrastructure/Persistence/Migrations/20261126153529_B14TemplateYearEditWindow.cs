using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class B14TemplateYearEditWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "YearEditWindow",
                schema: "cfg",
                table: "Template",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_Template_YearEditWindow");

            // ✎ b14 document-move (D-577, людина 25.11): HSE401 — таблиця вводу без подання й погодження; її річні дані
            // правляться у вікні «рік проєкту + зміщення». Діє лише в місячному проєкті (річний HSE401_2026 — як доти).
            // Немає шаблону HSE401 (свіжа база) — нічого.
            migrationBuilder.Sql(Hse401YearWindowSql);
        }

        /// <summary>Разове ввімкнення вікна року для шаблону HSE401 (рішення людини 25.11).</summary>
        internal const string Hse401YearWindowSql =
            "UPDATE cfg.Template SET YearEditWindow = 1 WHERE Code = N'HSE401' AND YearEditWindow = 0;";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "YearEditWindow",
                schema: "cfg",
                table: "Template")
                .Annotation("Relational:DefaultConstraintName", "DF_Template_YearEditWindow");
        }
    }
}
