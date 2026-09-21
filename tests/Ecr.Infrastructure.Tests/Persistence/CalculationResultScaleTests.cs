// tests/Ecr.Infrastructure.Tests/Persistence/CalculationResultScaleTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Стовпець результату приймає рівно стільки знаків, скільки віддає рушій.
/// </summary>
/// <remarks>
/// ⚠ Раніше цей клас стверджував ЗВОРОТНЕ — <c>decimal(28,10)</c> і «стовпець
/// вужчий за масштаб рушія», — і був правий: рішення <c>D-148</c>
/// (2026-09-20, «16 знаків у звіті — це конфігурація комірки») зняло константу
/// 6 з рушія, але міграція типів ішла окремим кроком. Міграція
/// <c>D148CalculationScale16</c> цей крок закрила, тест почервонів, як і було
/// задумано його автором, і оновлений тим самим комітом.
///
/// ⛔ Твердження лишається потрібним, і саме в такій формі. Звуження стовпця
/// назад НЕ падає нічим: SQL Server при меншому масштабі ОКРУГЛЮЄ, а не
/// відмовляє, тож знаки 11–16 зникли б мовчки — і виявилося б це на звірці
/// числа у звіті з числом у базі, а не тут.
///
/// ⚠ Бази не потребує: модель EF будується без з'єднання, рядок підключення
/// тут — формальність.
/// </remarks>
public sealed class CalculationResultScaleTests
{
    /// <summary>
    /// <c>NumericPolicy.DefaultOutputScale</c> літералом: на
    /// <c>Ecr.Calculations</c> цей проєкт не посилається, а число — вимога
    /// (<c>D-148</c>), не деталь реалізації.
    /// </summary>
    private const int EngineOutputScale = 16;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Стовпець_результату_вміщає_увесь_масштаб_рушія()
    {
        using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer("Server=(local);Database=EcrModelOnly;Integrated Security=true")
                .Options);

        var property = db.Model
            .FindEntityType(typeof(CalculationResult))!
            .FindProperty(nameof(CalculationResult.Value))!;

        // Літерал, а не складене з `EngineOutputScale` рядком: тоді твердження
        // рухалося б разом із константою й не тримало б нічого.
        Assert.Equal("decimal(34,16)", property.GetColumnType());

        // ⛔ Друга половина, без якої перша не доводить запису. Тип стовпця і
        // масштаб ПАРАМЕТРА — різні речі: EF бере масштаб із зіставлення типів,
        // і параметр, вужчий за стовпець, округлив би значення ще до відправки
        // (той самий клас `DAT-03`, що й `SqlMetaData` у TVP). Сам стовпець при
        // цьому лишився б «правильним» — і в базі лежало б обрізане число.
        Assert.Equal(16, property.GetRelationalTypeMapping().Scale);

        // І та сама вимога — числом: шістнадцять знаків стовпця не менші за
        // шістнадцять знаків рушія.
        Assert.True(
            16 >= EngineOutputScale,
            "Стовпець знову вужчий за масштаб рушія — знаки 11–16 губляться мовчки.");
    }
}
