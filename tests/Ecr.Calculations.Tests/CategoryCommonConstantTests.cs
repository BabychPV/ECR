// tests/Ecr.Calculations.Tests/CategoryCommonConstantTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// L-2: за заданої категорії рядка константа категорії «Common» теж кандидат (AF зберігає
/// спільні константи методології буквально як <c>Category = "Common"</c>).
/// </summary>
/// <remarks>
/// ⛔ Що було. Заданій категорії відповідав лише точний збіг, а далі — константи БЕЗ категорії.
/// Імпорт AF ставить спільним константам не <c>null</c>, а <c>"Common"</c>, тож щойно рушій
/// почав передавати категорію рядка, усі «Common»-константи (k1…k5, межі вибору) зникли б.
/// Без заданої категорії поведінка не змінюється: звуження немає.
///
/// Мутаційний доказ: резолвер без гілки «Common» — червоні
/// <see cref="Константа_Common_знаходиться_коли_категорія_рядка_задана"/> і
/// <see cref="Незнайдена_категорія_падає_на_Common"/>.
/// </remarks>
public sealed class CategoryCommonConstantTests
{
    private const int VersionId = 91;
    private static readonly DateOnly OnDate = new(2026, 1, 31);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Константа_Common_знаходиться_коли_категорія_рядка_задана()
    {
        var resolved = Resolve("B", Constant("K", 7m, "Common"));

        Assert.Equal(7m, resolved!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Точний_збіг_категорії_виграє_над_Common()
    {
        var resolved = Resolve("B", Constant("K", 3m, "B"), Constant("K", 7m, "Common"));

        Assert.Equal(3m, resolved!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Незнайдена_категорія_падає_на_Common()
    {
        var resolved = Resolve("C", Constant("K", 3m, "B"), Constant("K", 7m, "Common"));

        Assert.Equal(7m, resolved!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_Common_і_без_збігу_категорії_кандидата_немає()
    {
        // ⛔ Заповнювачів для відсутніх категорій НЕ створюємо: «немає значення для категорії» — це
        // #REF у формулі, а не вигадане число (L-2, пункт C).
        Assert.Null(Resolve("C", Constant("K", 3m, "B"), Constant("K", 4m, "A")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_заданої_категорії_Common_не_звужує_нічого()
    {
        // Контроль: category = null — як і було, без звуження; кілька кандидатів — неоднозначність.
        var ex = Assert.Throws<DomainException>(
            () => Resolve(null, Constant("K", 3m, "B"), Constant("K", 7m, "Common")));

        Assert.Equal("ECR-CALC-0422", ex.ErrorCode);
    }

    private static ResolvedConstant? Resolve(string? category, params MethodologyConstant[] candidates)
        => new ConstantResolver(Substitute.For<Ecr.Application.Ports.IConstantStore>())
            .Resolve(candidates, VersionId, "K", category, substanceEntryId: null, OnDate);

    private static MethodologyConstant Constant(string code, decimal value, string category)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 23);
        constant.SetScope(category, substanceEntryId: null);
        return constant;
    }
}
