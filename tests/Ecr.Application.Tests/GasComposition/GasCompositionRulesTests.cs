// tests/Ecr.Application.Tests/GasComposition/GasCompositionRulesTests.cs
using Ecr.Application.Errors;
using Ecr.Application.GasComposition;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.GasComposition;

/// <summary>
/// Шкала версій складу газу (D-NEW): дата дії — 1-ше число місяця, вікна кейсу без перетинів, вставка обрізає версію, що
/// містить дату, видалення продовжує попередню, замкнений період не змінюється.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не в коміті): (1) у <see cref="GasCompositionRules.PlanInsert"/> не обрізати хост (порожні
/// <c>Adjustments</c>) → <c>Вставка_всередину_обрізає_версію_і_діє_до_її_старого_кінця</c> червоний; (2) у
/// <see cref="GasCompositionRules.RequireUnlocked"/> повертатися завжди → обидва тести замкненого періоду червоні;
/// (3) прибрати <see cref="GasCompositionRules.RequireMonthStart"/> → <c>Дата_не_з_першого_числа_відмова</c> червоний;
/// (4) у <see cref="GasDateRange.Overlaps"/> «&lt;» → «&lt;=» → <c>Період_одразу_після_вікна_не_замкнений</c> червоний.
/// </remarks>
public sealed class GasCompositionRulesTests
{
    private static readonly DateOnly Jan22 = new(2022, 1, 1);
    private static readonly DateOnly Mar26 = new(2026, 3, 1);
    private static readonly DateOnly Jul26 = new(2026, 7, 1);

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Перша_версія_кейсу_відкрита()
    {
        var plan = GasCompositionRules.PlanInsert([], Mar26);

        Assert.Equal(new ValidityWindow(Mar26, null), plan.NewWindow);
        Assert.Empty(plan.Adjustments);
        Assert.Equal(new GasDateRange(Mar26, null), plan.Affected);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Вставка_всередину_обрізає_версію_і_діє_до_її_старого_кінця()
    {
        GasVersionSlot[] versions = [new(1, Jan22, Jul26), new(2, Jul26, null)];

        var plan = GasCompositionRules.PlanInsert(versions, Mar26);

        Assert.Equal(new ValidityWindow(Mar26, Jul26), plan.NewWindow);
        var cut = Assert.Single(plan.Adjustments);
        Assert.Equal(1, cut.Id);
        Assert.Equal(new ValidityWindow(Jan22, Mar26), cut.Window);
        Assert.Equal(new GasDateRange(Mar26, Jul26), plan.Affected);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Вставка_після_чинної_обрізає_чинну_і_лишається_відкритою()
    {
        GasVersionSlot[] versions = [new(1, Jan22, null)];

        var plan = GasCompositionRules.PlanInsert(versions, Mar26);

        Assert.Equal(new ValidityWindow(Mar26, null), plan.NewWindow);
        Assert.Equal(new ValidityWindow(Jan22, Mar26), Assert.Single(plan.Adjustments).Window);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Вставка_до_першої_діє_до_її_початку_без_змін_сусідів()
    {
        GasVersionSlot[] versions = [new(1, Mar26, null)];

        var plan = GasCompositionRules.PlanInsert(versions, Jan22);

        Assert.Equal(new ValidityWindow(Jan22, Mar26), plan.NewWindow);
        Assert.Empty(plan.Adjustments);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Дата_вже_зайнята_відмова_409()
    {
        GasVersionSlot[] versions = [new(1, Mar26, null)];

        var ex = Assert.Throws<BusinessRuleException>(() => GasCompositionRules.PlanInsert(versions, Mar26));

        Assert.Equal("ECR-REG-0409", ex.ErrorCode);
        Assert.Equal(GasCompositionRules.DateTakenKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Дата_не_з_першого_числа_відмова()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => GasCompositionRules.PlanInsert([], new DateOnly(2026, 3, 15)));

        Assert.Equal("ECR-REG-0422", ex.ErrorCode);
        Assert.Equal(GasCompositionRules.NotMonthStartKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Видалення_продовжує_попередню_на_вікно_видаленої()
    {
        GasVersionSlot[] versions = [new(1, Jan22, Mar26), new(2, Mar26, Jul26), new(3, Jul26, null)];

        var plan = GasCompositionRules.PlanDelete(versions, 2);

        Assert.Null(plan.NewWindow);
        var extended = Assert.Single(plan.Adjustments);
        Assert.Equal(1, extended.Id);
        Assert.Equal(new ValidityWindow(Jan22, Jul26), extended.Window);
        Assert.Equal(new GasDateRange(Mar26, Jul26), plan.Affected);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Видалення_першої_не_чіпає_сусідів()
    {
        GasVersionSlot[] versions = [new(1, Jan22, Mar26), new(2, Mar26, null)];

        var plan = GasCompositionRules.PlanDelete(versions, 1);

        Assert.Empty(plan.Adjustments);
        Assert.Equal(new GasDateRange(Jan22, Mar26), plan.Affected);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Перенесення_початку_тягне_попередню_а_зачеплене_між_датами()
    {
        GasVersionSlot[] versions = [new(1, Jan22, Mar26), new(2, Mar26, null)];

        var plan = GasCompositionRules.PlanMove(versions, 2, Jul26, valuesChanged: false);

        Assert.Equal(new ValidityWindow(Jul26, null), plan.NewWindow);
        Assert.Equal(new ValidityWindow(Jan22, Jul26), Assert.Single(plan.Adjustments).Window);
        Assert.Equal(new GasDateRange(Mar26, Jul26), plan.Affected);

        var withValues = GasCompositionRules.PlanMove(versions, 2, Jul26, valuesChanged: true);
        Assert.Equal(new GasDateRange(Mar26, null), withValues.Affected);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Перенесення_за_сусіда_відмова()
    {
        GasVersionSlot[] versions = [new(1, Jan22, Mar26), new(2, Mar26, Jul26), new(3, Jul26, null)];

        var ex = Assert.Throws<BusinessRuleException>(() => GasCompositionRules.PlanMove(versions, 2, Jul26, valuesChanged: false));
        Assert.Equal(GasCompositionRules.DateOutOfRangeKey, ex.Details!["messageKey"]);

        var before = Assert.Throws<BusinessRuleException>(() => GasCompositionRules.PlanMove(versions, 2, Jan22, valuesChanged: false));
        Assert.Equal(GasCompositionRules.DateOutOfRangeKey, before.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Зміна_у_замкненому_місяці_відмова_з_першим_відкритим()
    {
        GasLockedPeriod[] locked = [new(202604, GasLockedPeriod.Approved), new(202605, GasLockedPeriod.Closed)];

        var ex = Assert.Throws<BusinessRuleException>(() =>
            GasCompositionRules.RequireUnlocked(new GasDateRange(Mar26, null), locked));

        Assert.Equal("ECR-REG-0409", ex.ErrorCode);
        Assert.Equal(GasCompositionRules.LockedKey, ex.Details!["messageKey"]);
        Assert.Equal("2026-04, 2026-05", ex.Details["periods"]);
        Assert.Equal("2026-06-01", ex.Details["firstOpen"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Річний_замкнений_період_замикає_увесь_рік()
    {
        GasLockedPeriod[] locked = [new(202500, GasLockedPeriod.Closed)];

        Assert.Throws<BusinessRuleException>(() =>
            GasCompositionRules.RequireUnlocked(new GasDateRange(new DateOnly(2025, 11, 1), new DateOnly(2025, 12, 1)), locked));
        GasCompositionRules.RequireUnlocked(new GasDateRange(new DateOnly(2026, 1, 1), null), locked);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Період_одразу_після_вікна_не_замкнений()
    {
        GasLockedPeriod[] locked = [new(202603, GasLockedPeriod.Approved)];

        GasCompositionRules.RequireUnlocked(new GasDateRange(Jan22, Mar26), locked);

        Assert.Empty(GasCompositionRules.LocksOf(new GasDateRange(Jan22, Mar26), locked));
        Assert.Single(GasCompositionRules.LocksOf(new GasDateRange(Jan22, Jul26), locked));
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Роки_зачепленого_діапазону()
    {
        Assert.Equal([2025, 2026], GasCompositionRules.YearsOf(new GasDateRange(new DateOnly(2025, 12, 1), new DateOnly(2026, 2, 1)), 2030));
        Assert.Equal([2026], GasCompositionRules.YearsOf(new GasDateRange(Mar26, Jul26), 2030));
        Assert.Equal([2026, 2027], GasCompositionRules.YearsOf(new GasDateRange(Mar26, null), 2027));
    }
}
