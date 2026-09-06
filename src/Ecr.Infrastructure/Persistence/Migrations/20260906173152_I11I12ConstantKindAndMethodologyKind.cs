using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Константа не завжди число, формула не завжди повертає число, а
    /// методологія не замкнена (директива ПК-1 №05, поправки 2, 2-біс, 6, 10).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>MethodologyConstant.Value</c> стає <c>NULL</c>-придатною, а
    /// <c>UnitId</c> — необов'язковою. Без цього 108 нечислових констант
    /// корпусу (≈90 із них ужиті у виразах) неможливо ні зберегти, ні
    /// відрізнити від нуля: перевернуте правило пакета «нечислове значення —
    /// помилка публікації» відхилило б їх усі й зламало б механізм категорій.
    ///
    /// ⛔ <c>CK_MC_Kind</c> навмисно ДОЗВОЛЯЄ <c>Kind = 0</c> з порожнім
    /// <c>Value</c>, якщо є <c>TextValue</c>. Це рядок, який імпорт не зміг
    /// розібрати (<c>n_ECW_C11_13_ = '-'</c> у 16 формулах,
    /// <c>Kp_ECW_C11_13_ = '-'</c> у 4, <c>k22_HSE30X_Int_FG_ = ''</c> у 5), і
    /// він має дожити до публікації, де стане рядком у переліку проблем.
    /// Заборонити його тут означало б або тихий нуль у 25 формулах, або обрив
    /// імпорту 6507 констант на трьох дефектних рядках.
    ///
    /// ⛔ <c>calc.MethodologyImport</c> і <c>calc.MethodologyDependency</c> —
    /// пара, а не дві незалежні таблиці: перша дає резолвінг <c>!Name</c> через
    /// межу методології (149 посилань із <c>HSE400</c> і 116 з <c>Flert</c>
    /// ведуть у <c>Common</c>), друга — порядок перерахунку для тих самих
    /// посилань. Без другої <c>HSE400</c> читає торішній результат
    /// <c>Common</c>, і в журналі про це немає нічого.
    ///
    /// ⚠ Значення за замовчуванням у БАЗІ (<c>0</c>) для трьох нових
    /// перелічень: наявні рядки — це числові константи, числові формули і
    /// <c>DataDriven</c> методології, і міграція не змінює поведінки жодного з
    /// них. У МОДЕЛІ default не оголошено — інакше EF пропускав би властивість
    /// при вставці саме тоді, коли її значення дорівнює нулю.
    ///
    /// ⚠ <c>Down</c> повертає <c>NOT NULL</c> із <c>defaultValue: 0</c>: відкат
    /// перетворить нечислову константу на нуль. Це не недогляд EF, а причина
    /// не відкочувати цю міграцію на базі з імпортованим корпусом.
    /// </remarks>
    public partial class I11I12ConstantKindAndMethodologyKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<byte>(
                name: "ResultType",
                schema: "calc",
                table: "MethodologyFormula",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "MethodologyConstant",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,10)",
                oldPrecision: 28,
                oldScale: 10);

            migrationBuilder.AlterColumn<int>(
                name: "UnitId",
                schema: "calc",
                table: "MethodologyConstant",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                schema: "calc",
                table: "MethodologyConstant",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<string>(
                name: "TextValue",
                schema: "calc",
                table: "MethodologyConstant",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                schema: "calc",
                table: "Methodology",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "MethodologyDependency",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FromMethodologyId = table.Column<int>(type: "int", nullable: false),
                    ToMethodologyId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyDependency", x => x.Id);
                    table.CheckConstraint("CK_MD_NoSelfLoop", "FromMethodologyId <> ToMethodologyId");
                    table.ForeignKey(
                        name: "FK_MD_From",
                        column: x => x.FromMethodologyId,
                        principalSchema: "calc",
                        principalTable: "Methodology",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MD_To",
                        column: x => x.ToMethodologyId,
                        principalSchema: "calc",
                        principalTable: "Methodology",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MethodologyImport",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyVersionId = table.Column<int>(type: "int", nullable: false),
                    ImportedMethodologyId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MethodologyImport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MI_Imported",
                        column: x => x.ImportedMethodologyId,
                        principalSchema: "calc",
                        principalTable: "Methodology",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MI_Version",
                        column: x => x.MethodologyVersionId,
                        principalSchema: "calc",
                        principalTable: "MethodologyVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MF_TextHasNoUnit",
                schema: "calc",
                table: "MethodologyFormula",
                sql: "ResultType = 0 OR OutputUnitId IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MC_Kind",
                schema: "calc",
                table: "MethodologyConstant",
                sql: "(Kind = 0 AND UnitId IS NOT NULL AND (Value IS NOT NULL OR TextValue IS NOT NULL)) OR (Kind <> 0 AND Value IS NULL AND UnitId IS NULL AND TextValue IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyDependency",
                schema: "calc",
                table: "MethodologyDependency",
                columns: new[] { "FromMethodologyId", "ToMethodologyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyImport",
                schema: "calc",
                table: "MethodologyImport",
                columns: new[] { "MethodologyVersionId", "ImportedMethodologyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "MethodologyDependency",
                schema: "calc");

            migrationBuilder.DropTable(
                name: "MethodologyImport",
                schema: "calc");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MF_TextHasNoUnit",
                schema: "calc",
                table: "MethodologyFormula");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MC_Kind",
                schema: "calc",
                table: "MethodologyConstant");

            migrationBuilder.DropColumn(
                name: "ResultType",
                schema: "calc",
                table: "MethodologyFormula");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "calc",
                table: "MethodologyConstant");

            migrationBuilder.DropColumn(
                name: "TextValue",
                schema: "calc",
                table: "MethodologyConstant");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "calc",
                table: "Methodology");

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "MethodologyConstant",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,10)",
                oldPrecision: 28,
                oldScale: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "UnitId",
                schema: "calc",
                table: "MethodologyConstant",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
