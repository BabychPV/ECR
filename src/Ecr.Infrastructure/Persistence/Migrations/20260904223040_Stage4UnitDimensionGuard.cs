using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Заборона конверсій між різними розмірностями на рівні БД
    /// (<c>02a-db-schema.md</c> §4, ФВ-16.5, <c>D-75</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Це те, що не дає щільності пролізти в таблицю конверсій. Домовленості
    /// тут недостатньо: коефіцієнт «м³ → т» виглядає як звичайна конверсія і
    /// вставляється одним <c>INSERT</c>, після чого те саме число починає
    /// перетворюватися по-різному залежно від того, хто заповнив довідник.
    /// <para>
    /// Скалярна функція з <c>SCHEMABINDING</c> моделлю EF не виражається, тому
    /// міграція написана вручну. Порожню її згенерував <c>ef migrations add</c>
    /// — модель не змінилася, змінилася лише фізична схема.
    /// </para>
    /// </remarks>
    public partial class Stage4UnitDimensionGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // SCHEMABINDING обов'язковий: без нього функцію можна зробити
            // недійсною, змінивши uom.Unit, і обмеження тихо перестало б
            // працювати — саме той клас дефекту, проти якого воно й стоїть.
            migrationBuilder.Sql("""
                CREATE FUNCTION uom.fnSameDimension (@from int, @to int)
                RETURNS bit
                WITH SCHEMABINDING
                AS
                BEGIN
                    DECLARE @r bit = 0;
                    SELECT @r = CASE WHEN f.DimensionId = t.DimensionId THEN 1 ELSE 0 END
                    FROM uom.Unit f CROSS JOIN uom.Unit t
                    WHERE f.Id = @from AND t.Id = @to;
                    RETURN ISNULL(@r, 0);
                END;
                """);

            // ISNULL(@r, 0) у функції означає: невідома одиниця теж не
            // проходить. Відсутність рядка — не «немає заперечень».
            migrationBuilder.Sql("""
                ALTER TABLE uom.Conversion
                    ADD CONSTRAINT CK_Conv_SameDimension
                        CHECK (uom.fnSameDimension(FromUnitId, ToUnitId) = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Порядок зворотний: доки обмеження посилається на функцію,
            // SCHEMABINDING не дає її прибрати.
            migrationBuilder.Sql("ALTER TABLE uom.Conversion DROP CONSTRAINT CK_Conv_SameDimension;");
            migrationBuilder.Sql("DROP FUNCTION uom.fnSameDimension;");
        }
    }
}
