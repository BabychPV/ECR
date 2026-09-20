// tests/Ecr.Infrastructure.Tests/Persistence/CalculationResultScaleTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Рушій віддає шістнадцять знаків, а стовпець приймає десять — і це видно
/// звідси, а не з'ясовується на звірці.
/// </summary>
/// <remarks>
/// ⛔ Тест НЕ стверджує, що <c>decimal(28,10)</c> — правильний тип. Він робить
/// ВИДИМОЮ залежність, яку рішення <c>D-148</c> (2026-09-20, «має бути 16
/// знаків у звіті, це конфігурація комірки») лишило відкритою: масштаб виходу
/// методології більше не константа 6, але міграція типів колонок — окремий
/// крок, і доти знаки 11–16 не доживають до <c>calc.CalculationResult</c>.
///
/// ⚠ Губляться вони МОВЧКИ: SQL Server при звуженні масштабу округлює, а не
/// відмовляє. Саме тому знати про це має тест, а не той, хто через місяць
/// звірятиме число у звіті з числом у базі.
///
/// ⚠ Коли міграція переведе стовпець на <c>decimal(28,16)</c>, цей тест
/// почервоніє — і це його робота: оновити його треба ТИМ САМИМ комітом, що й
/// тип, інакше залишиться перевірка, яка стереже вчорашній стан.
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
    public void Стовпець_результату_вужчий_за_масштаб_рушія()
    {
        using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer("Server=(local);Database=EcrModelOnly;Integrated Security=true")
                .Options);

        var columnType = db.Model
            .FindEntityType(typeof(CalculationResult))!
            .FindProperty(nameof(CalculationResult.Value))!
            .GetColumnType();

        Assert.Equal("decimal(28,10)", columnType);

        // Десять < шістнадцяти — тобто залежність від міграції реальна, а не
        // теоретична.
        Assert.True(
            10 < EngineOutputScale,
            "Стовпець уже не вужчий за масштаб рушія — міграцію накочено, тест треба оновити.");
    }
}
