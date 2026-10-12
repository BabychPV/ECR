using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class L6ConditionalFormatScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CondFmt_Operator",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.AddColumn<string>(
                name: "Pattern",
                schema: "cfg",
                table: "ConditionalFormatRule",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefColumnCode",
                schema: "cfg",
                table: "ConditionalFormatRule",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowKeys",
                schema: "cfg",
                table: "ConditionalFormatRule",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                schema: "cfg",
                table: "ConditionalFormatRule",
                type: "varchar(8)",
                unicode: false,
                maxLength: 8,
                nullable: false,
                defaultValue: "column")
                .Annotation("Relational:DefaultConstraintName", "DF_CondFmt_Scope");

            migrationBuilder.AddColumn<string>(
                name: "TableCode",
                schema: "cfg",
                table: "ConditionalFormatRule",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CondFmt_Table",
                schema: "cfg",
                table: "ConditionalFormatRule",
                columns: new[] { "TemplateVersionId", "TableCode" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CondFmt_AnyColumn",
                schema: "cfg",
                table: "ConditionalFormatRule",
                sql: "ColumnCode <> N'*' OR TableCode IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CondFmt_Operator",
                schema: "cfg",
                table: "ConditionalFormatRule",
                sql: "Operator IN (N'gt', N'ge', N'lt', N'le', N'eq', N'ne', N'between', N'empty', N'notEmpty', N'textEq', N'textNe')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CondFmt_Pattern",
                schema: "cfg",
                table: "ConditionalFormatRule",
                sql: "Pattern IS NULL OR Pattern = N'hatch'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CondFmt_RowScope",
                schema: "cfg",
                table: "ConditionalFormatRule",
                sql: "(Scope = N'row' AND TableCode IS NOT NULL AND RowKeys IS NOT NULL) OR (Scope = N'column' AND RowKeys IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CondFmt_Scope",
                schema: "cfg",
                table: "ConditionalFormatRule",
                sql: "Scope IN (N'column', N'row')");
        }

        /// <inheritdoc />
        /// <remarks>
        /// ⚠ Down лише на базі БЕЗ правил з textEq/textNe: повернення вузького CK_CondFmt_Operator впаде на таких
        /// рядках. Спершу видаліть їх (або правила з областю) — дані автоматично не губляться.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CondFmt_Table",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CondFmt_AnyColumn",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CondFmt_Operator",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CondFmt_Pattern",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CondFmt_RowScope",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CondFmt_Scope",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropColumn(
                name: "Pattern",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropColumn(
                name: "RefColumnCode",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropColumn(
                name: "RowKeys",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "cfg",
                table: "ConditionalFormatRule")
                .Annotation("Relational:DefaultConstraintName", "DF_CondFmt_Scope");

            migrationBuilder.DropColumn(
                name: "TableCode",
                schema: "cfg",
                table: "ConditionalFormatRule");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CondFmt_Operator",
                schema: "cfg",
                table: "ConditionalFormatRule",
                sql: "Operator IN (N'gt', N'ge', N'lt', N'le', N'eq', N'ne', N'between', N'empty', N'notEmpty')");
        }
    }
}
