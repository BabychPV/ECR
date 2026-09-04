// tests/Ecr.Expressions.Tests/Binding/UnitCheckerTests.cs
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Перевірка одиниць при публікації — **головна цінність механізму одиниць**:
/// помилка ловиться до продуктиву, а не на звірці через місяць (ФВ-16.7).
/// </summary>
public sealed class UnitCheckerTests
{
    /// <summary>Розмірність «маса»; номери збігаються з <c>uom.Dimension</c> у seed.</summary>
    private const byte Mass = 1;

    /// <summary>Розмірність «об'єм».</summary>
    private const byte Volume = 2;

    /// <summary>Розмірність «час».</summary>
    private const byte Time = 4;

    /// <summary>Розмірність «масова витрата» — похідна.</summary>
    private const byte MassFlow = 8;

    private const int Kg = 1;
    private const int Tonne = 8;
    private const int Gram = 9;
    private const int CubicMetre = 2;
    private const int Second = 4;
    private const int GramPerSecond = 20;

    private static readonly UnitChecker Checker = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Додавання_величин_у_різних_одиницях_відхиляється_з_ECR_TMPL_4223()
    {
        var context = Context();
        context.ColumnUnits["MassT"] = Tonne;
        context.ColumnUnits["MassKg"] = Kg;

        var (_, diagnostics) = Check("[MassT] + [MassKg]", context);

        // ⚠ Тонни плюс кілограми — це не «приблизно правильно», це число,
        // помножене на тисячу. Excel тут порахував би мовчки (ФВ-16.4, D-74).
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4223");

        // Та сама одиниця з обох боків — жодного зауваження.
        Assert.Empty(Check("[MassT] + [MassT]", context).Diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Додавання_після_явного_CONVERT_проходить()
    {
        var context = Context();
        context.ColumnUnits["MassT"] = Tonne;
        context.ColumnUnits["MassKg"] = Kg;

        var (unit, diagnostics) = Check("CONVERT([MassT], 't', 'kg') + [MassKg]", context);

        // ⛔ Ось єдиний спосіб поєднати різні одиниці — написати конверсію
        // руками. Механізм не «полегшує» це: він вимагає, щоб автор формули
        // сказав, у чому саме він хоче результат.
        Assert.Empty(diagnostics);
        Assert.Equal(Kg, unit);

        // І напрям має значення: CONVERT у ГРАМИ з подальшим додаванням
        // кілограмів так само відхиляється.
        Assert.Contains(
            Check("CONVERT([MassT], 't', 'g') + [MassKg]", context).Diagnostics,
            d => d.Code == "ECR-TMPL-4223");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Ділення_маси_на_час_дає_похідну_одиницю_масової_витрати()
    {
        var context = Context();
        context.ColumnUnits["MassG"] = Gram;
        context.ColumnUnits["Duration"] = Second;
        context.Derived[$"{Gram}|{Second}"] = GramPerSecond;

        var (unit, diagnostics) = Check("[MassG] / [Duration]", context);

        // Похідна одиниця береться з довідника ПОСИЛАННЯМИ на чисельник і
        // знаменник (ФВ-16.2), а не складається з рядка «g/s».
        Assert.Empty(diagnostics);
        Assert.Equal(GramPerSecond, unit);

        // ⚠ Якщо такої похідної одиниці в довіднику немає — це помилка
        // публікації, а не привід вигадати їй ідентифікатор.
        var missing = Context();
        missing.ColumnUnits["MassG"] = Gram;
        missing.ColumnUnits["Duration"] = Second;

        Assert.Contains(
            Check("[MassG] / [Duration]", missing).Diagnostics,
            d => d.Code == "ECR-TMPL-4223");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Результат_несумісний_з_оголошеною_одиницею_колонки_відхиляється()
    {
        var context = Context();
        context.ColumnUnits["MassKg"] = Kg;
        context.ColumnUnits["VolumeM3"] = CubicMetre;

        // Маса й об'єм — різні розмірності. Це не «інша одиниця», яку можна
        // привести: коефіцієнт між ними залежить від речовини (ФВ-16.5).
        var (_, diagnostics) = Check("[MassKg] + [VolumeM3]", context);

        var mismatch = Assert.Single(diagnostics);
        Assert.Equal("ECR-TMPL-4223", mismatch.Code);
        Assert.Contains("розмірност", mismatch.Message, StringComparison.Ordinal);

        // І сам CONVERT між розмірностями теж відхиляється — на тій самій
        // підставі, що й у базі (CK_Conv_SameDimension).
        Assert.Contains(
            Check("CONVERT([VolumeM3], 'm3', 'kg')", context).Diagnostics,
            d => d.Code == "ECR-TMPL-4223");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Агрегація_колонки_з_одиницею_на_рядок_без_приведення_відхиляється()
    {
        var context = Context();

        // ФВ-16.8: колонка `DataType = Unit` — одиниця лежить у КОЖНІЙ комірці
        // (`doc.CellValue.ValueUnitId`, R-A4, D-87).
        context.RowScopedUnitColumns.Add("Amount");

        var (_, diagnostics) = Check("SUM([Amount])", context);

        // ⛔ Тут навіть немає двох одиниць, які видно у виразі: SUM склав би
        // тонни з кілограмами, не давши жодного натяку. Саме тому перевірка
        // дивиться на ОПИС колонки, а не на одиниці операндів.
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4223");

        // Приведення знімає заборону: під CONVERT усе вже в одній одиниці.
        context.UnitsByCode["kg"] = Kg;
        Assert.Empty(Check("SUM(CONVERT([Amount], 't', 'kg'))", context).Diagnostics);

        // А звичайна колонка з однією одиницею агрегується без зауважень.
        context.ColumnUnits["MassKg"] = Kg;
        Assert.Empty(Check("SUM([MassKg])", context).Diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Неявної_конверсії_не_відбувається_навіть_коли_вона_очевидна()
    {
        var context = Context();
        context.ColumnUnits["MassT"] = Tonne;
        context.ColumnUnits["MassKg"] = Kg;

        // ⛔ Тонни й кілограми — одна розмірність, коефіцієнт відомий, напрям
        // очевидний. Рушій усе одно відмовляє (D-74).
        Assert.Contains(
            Check("[MassT] + [MassKg]", context).Diagnostics,
            d => d.Code == "ECR-TMPL-4223");

        // ⚠ І множення на 1000 конверсією НЕ вважається: літерал безрозмірний,
        // тож `[MassT] * 1000` лишається в тоннах. Якби він мовчки ставав
        // кілограмами, кожен звіт мав би власну домовленість про множники —
        // рівно те, від чого система відходить.
        var (unit, diagnostics) = Check("[MassT] * 1000", context);
        Assert.Empty(diagnostics);
        Assert.Equal(Tonne, unit);

        Assert.Contains(
            Check("[MassT] * 1000 + [MassKg]", context).Diagnostics,
            d => d.Code == "ECR-TMPL-4223");
    }

    private static (int? Unit, List<ExpressionDiagnostic> Diagnostics) Check(
        string expression, TestBindingContext context)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        var unit = Checker.Check(parsed.Expression!.Root, context, diagnostics);
        return (unit, diagnostics);
    }

    /// <summary>Контекст із розмірностями і кодами одиниць за seed-ом.</summary>
    private static TestBindingContext Context()
    {
        var context = new TestBindingContext();

        context.Dimensions[Kg] = Mass;
        context.Dimensions[Tonne] = Mass;
        context.Dimensions[Gram] = Mass;
        context.Dimensions[CubicMetre] = Volume;
        context.Dimensions[Second] = Time;
        context.Dimensions[GramPerSecond] = MassFlow;

        context.UnitsByCode["kg"] = Kg;
        context.UnitsByCode["t"] = Tonne;
        context.UnitsByCode["g"] = Gram;
        context.UnitsByCode["m3"] = CubicMetre;
        context.UnitsByCode["s"] = Second;

        return context;
    }
}
