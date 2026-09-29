// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyLibraryContentTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// HSE301 L2: <see cref="MethodologyStore.GetLibraryContentsAsync"/> віддає склад
/// САМЕ тієї версії бібліотеки, що чинна на дату.
/// </summary>
/// <remarks>
/// ⛔ Дві опубліковані версії <c>Common</c> з різними числами: перерахунок минулого року
/// мусить узяти торішню редакцію, а не сьогоднішню (ФВ-9.3). Мутаційний доказ: брати
/// найновішу опубліковану версію замість «останньої з <c>EffectiveFrom ≤ дата</c>» —
/// <see cref="Склад_бібліотеки_тієї_версії_що_чинна_на_дату"/> червоний (на 2025-06-30
/// приходить «2» і 0.7 замість «1» і 0.5).
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyLibraryContentTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("2025-06-30", "1", "0.5", NumericMode.Strict, CalendarMode.Fixed365)]
    [InlineData("2026-03-31", "2", "0.7", NumericMode.Legacy, CalendarMode.Actual)]
    public async Task Склад_бібліотеки_тієї_версії_що_чинна_на_дату(
        string onDate, string expression, string constant, NumericMode numeric, CalendarMode calendar)
    {
        var seed = await SeedAsync();
        await using var db = sql.CreateContext();

        var contents = await new MethodologyStore(db)
            .GetLibraryContentsAsync(seed.CallerVersionId, DateOnly.Parse(onDate, System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);

        var content = Assert.Single(contents);
        var expectedVersion = expression == "1" ? seed.FirstVersionId : seed.SecondVersionId;

        Assert.Equal(seed.CommonId, content.Library.MethodologyId);
        Assert.Equal(expectedVersion, content.Library.MethodologyVersionId);
        Assert.Equal(numeric, content.NumericMode);
        Assert.Equal(calendar, content.CalendarMode);

        var formula = Assert.Single(content.Formulas);
        Assert.Equal("Common_X", formula.Code);
        Assert.Equal(expression, formula.Expression);
        Assert.Equal(expectedVersion, formula.MethodologyVersionId);

        var ef = Assert.Single(content.Constants);
        Assert.Equal(decimal.Parse(constant, System.Globalization.CultureInfo.InvariantCulture), ef.Value);

        // ⚠ Без відстеження: склад чужої версії не потрапляє в SaveChanges викликача.
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Імпорт_без_чинної_версії_повертається_з_порожнім_складом()
    {
        // «Бібліотеки не видно» і «бібліотеки не оголошено» — різні проблеми.
        var seed = await SeedAsync();
        await using var db = sql.CreateContext();

        var content = Assert.Single(await new MethodologyStore(db)
            .GetLibraryContentsAsync(seed.CallerVersionId, new DateOnly(2024, 6, 30), CancellationToken.None));

        Assert.Equal(seed.CommonId, content.Library.MethodologyId);
        Assert.Null(content.Library.MethodologyVersionId);
        Assert.Null(content.NumericMode);
        Assert.Empty(content.Formulas);
        Assert.Empty(content.Constants);
    }

    private sealed record Seeded(int CommonId, int FirstVersionId, int SecondVersionId, int CallerVersionId);

    /// <summary>
    /// <c>Common</c> з двома опублікованими версіями (2025-01-01: X = 1, EF = 0.5;
    /// 2026-01-01: X = 2, EF = 0.7) і чернетка викликача, що її імпортує.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var db = sql.CreateContext();

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var common = new Methodology(EcrCode.Create($"LIBC_{tag}"), Name($"Common {tag}"));
        common.SetKind(MethodologyKind.Library);
        var caller = new Methodology(EcrCode.Create($"LIBU_{tag}"), Name($"Caller {tag}"));
        db.Methodologies.AddRange(common, caller);
        await db.SaveChangesAsync();

        var first = new MethodologyVersion(common.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        first.SetModes(NumericMode.Strict, CalendarMode.Fixed365, TraceLevel.Full);
        var second = new MethodologyVersion(common.Id, "2.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        second.SetModes(NumericMode.Legacy, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        var callerVersion = new MethodologyVersion(caller.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.AddRange(first, second, callerVersion);
        await db.SaveChangesAsync();

        db.MethodologyFormulas.AddRange(
            first.AddFormula(EcrCode.Create("Common_X"), "1", FormulaResultType.Number, unit),
            second.AddFormula(EcrCode.Create("Common_X"), "2", FormulaResultType.Number, unit));
        db.MethodologyConstants.AddRange(
            first.AddNumericConstant(EcrCode.Create("EF"), 0.5m, unit),
            second.AddNumericConstant(EcrCode.Create("EF"), 0.7m, unit));
        db.MethodologyImports.Add(new MethodologyImport(callerVersion.Id, common.Id, caller.Id));
        await db.SaveChangesAsync();

        var aggregate = await db.Methodologies.Include(m => m.Versions).FirstAsync(m => m.Id == common.Id);
        aggregate.PublishVersion(
            aggregate.Versions.Single(v => v.Id == first.Id), 2, "Перша редакція", new DateOnly(2025, 1, 1), true, Now);
        aggregate.PublishVersion(
            aggregate.Versions.Single(v => v.Id == second.Id), 2, "Друга редакція", new DateOnly(2026, 1, 1), true, Now);
        await db.SaveChangesAsync();

        return new Seeded(common.Id, first.Id, second.Id, callerVersion.Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
