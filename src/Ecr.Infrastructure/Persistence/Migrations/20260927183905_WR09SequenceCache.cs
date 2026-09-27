using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// `WR-09`: <c>CACHE 1000</c> для трьох послідовностей, з яких беруться
    /// Id партиційованих таблиць і результатів розрахунку.
    /// </summary>
    /// <remarks>
    /// Сирим SQL, бо EF Core 10 не має API кешу послідовності
    /// (<c>SequenceBuilder</c> — лише StartsAt/IncrementsBy/HasMin/HasMax/
    /// IsCyclic), тож модель і знімок моделі цього не бачать.
    ///
    /// ⚠ Стан ДО міграції — не «без кешу»: <c>CreateSequence</c> від EF не
    /// пише CACHE, і SQL Server бере власний розмір кешу
    /// (<c>is_cached = 1</c>, <c>cache_size = NULL</c>). Тому <c>Down</c>
    /// повертає саме <c>CACHE</c> без числа, а не <c>NO CACHE</c>.
    ///
    /// Наслідок кешу: при аварійній зупинці SQL Server невидані значення
    /// кешу губляться — у Id з'являються дірки до 1000. Код покладається лише
    /// на безперервність ОДНОГО діапазону <c>sp_sequence_get_range</c>, а її
    /// кеш не порушує.
    /// </remarks>
    public partial class WR09SequenceCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER SEQUENCE [doc].[TableInstanceSeq] CACHE 1000;");
            migrationBuilder.Sql("ALTER SEQUENCE [doc].[TableRowSeq] CACHE 1000;");
            migrationBuilder.Sql("ALTER SEQUENCE [calc].[CalculationResultSeq] CACHE 1000;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER SEQUENCE [doc].[TableInstanceSeq] CACHE;");
            migrationBuilder.Sql("ALTER SEQUENCE [doc].[TableRowSeq] CACHE;");
            migrationBuilder.Sql("ALTER SEQUENCE [calc].[CalculationResultSeq] CACHE;");
        }
    }
}
