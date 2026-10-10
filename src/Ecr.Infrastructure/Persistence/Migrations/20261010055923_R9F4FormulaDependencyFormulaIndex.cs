using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class R9F4FormulaDependencyFormulaIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FormulaDependency_Formula",
                schema: "cfg",
                table: "FormulaDependency",
                columns: new[] { "FormulaDefId", "SortOrder" })
                .Annotation("SqlServer:Include", new[] { "SourceKind", "BindingId", "DependsOnKind", "TableDefId", "RowKey", "ColumnDefId", "FilterJson", "PeriodOffset" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FormulaDependency_Formula",
                schema: "cfg",
                table: "FormulaDependency");
        }
    }
}
