using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Шість видів правил доступу до періоду замість одного (<c>ФВ-2.15</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ У таблиці жив лише статичний діапазон номерів періодів, тобто з
    /// шести механізмів чинного рішення переносився один — і разом із рештою
    /// зникало те єдине, що потрібне <c>ФВ-5.20</c>: вікно, взяте з довідника
    /// (<c>ApplyPermitMonthLocks</c>).
    ///
    /// ⚠ <c>defaultValue: 2</c> — <c>EditablePeriodOnly</c>: саме так
    /// поводяться наявні правила з <c>FromSequence</c>/<c>ToSequence</c>.
    /// Міграція не змінює поведінки жодного з них. У МОДЕЛІ значення за
    /// замовчуванням НЕ оголошене навмисно: інакше EF пропускав би
    /// властивість при вставці, коли її значення дорівнює нулю
    /// (<c>AlwaysReadOnly</c>), і база мовчки записувала б туди двійку.
    ///
    /// ⛔ <c>CK_PAR_Kind</c> тримає обов'язковість параметра на кожен вид.
    /// Правило <c>SourceWindow</c> без колонки-джерела не блокує нічого і
    /// виглядає працездатним — рівно та мовчазна порожнеча, яку виловлював
    /// увесь <c>A7</c>.
    /// </remarks>
    public partial class D146PeriodAccessRuleKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "ConditionExpr",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "RelativeOffset",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "RowKind",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "RuleKind",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)2);

            migrationBuilder.AddColumn<int>(
                name: "SourceColumnDefId",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PAR_Kind",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                sql: "(RuleKind = 4 AND SourceColumnDefId IS NOT NULL) OR (RuleKind = 3 AND RelativeOffset IS NOT NULL) OR (RuleKind = 5 AND ConditionExpr IS NOT NULL) OR RuleKind IN (0, 1, 2)");

            migrationBuilder.AddForeignKey(
                name: "FK_PAR_SourceColumn",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                column: "SourceColumnDefId",
                principalSchema: "cfg",
                principalTable: "ColumnDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_PAR_SourceColumn",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PAR_Kind",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropColumn(
                name: "ConditionExpr",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropColumn(
                name: "RelativeOffset",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropColumn(
                name: "RowKind",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropColumn(
                name: "RuleKind",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropColumn(
                name: "SourceColumnDefId",
                schema: "cfg",
                table: "PeriodAccessRuleDef");
        }
    }
}
