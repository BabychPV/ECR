// tests/Ecr.Api.Tests/CurrentSubstanceSymbolSqlTests.cs
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Символ «поточна речовина» <c>CURRENT_SUBSTANCE()</c> на РЕАЛЬНОМУ SQL Server: справжні
/// <see cref="MethodologyStore"/> (склад речовин + код з поля <c>SUBSTANCE_CODE</c>), <see cref="RegistryStore"/> і
/// <see cref="RegistrySnapshotLoader"/> (довідник складу з складеним ключем) під справжнім
/// <see cref="GenericCalculationModule"/>.
/// </summary>
/// <remarks>
/// Значень прод-даних немає: числа синтетичні. Перевіряється правило адресації: той самий рядок і та сама
/// формула дають різні числа для двох речовин версії, ключ показника будується з коду ПОЛЯ
/// <c>SUBSTANCE_CODE</c> (а не з коду запису <c>E301</c>), версія без складу речовин не отримує ключа.
/// Мутаційні докази (лише цей клас): у <c>MethodologyStore.GetSubstanceCodesAsync</c> брати код запису замість
/// поля — червоніє <see cref="Дві_речовини_складу_версії_дають_два_різних_результати_одного_рядка"/>;
/// не передавати код у контекст циклу речовин — червоніють обидва перші тести.
/// </remarks>
[Collection("SqlServer")]
public sealed class CurrentSubstanceSymbolSqlTests(SqlServerFixture sql)
{
    private const string Mass =
        "@X * REGFIELD(REGFIND('{COMP}', [Period].Year, [Period].Sequence, @Stream, CURRENT_SUBSTANCE() + '_mass'), 'VALUE')";

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_речовини_складу_версії_дають_два_різних_результати_одного_рядка()
    {
        var f = await ArrangeAsync(withSubstances: true);

        var output = await RunAsync(f, month: 1);

        // E301 -> SUBSTANCE_CODE 301 -> показник 301_mass; E304 -> 304_mass.
        Assert.Equal(0.9m * 10m, Value(output, f.Substance301));
        Assert.Equal(0.1m * 10m, Value(output, f.Substance304));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Період_рядка_вибирає_місяць_а_відсутній_показник_речовини_дає_NA_без_результату()
    {
        var f = await ArrangeAsync(withSubstances: true);

        var output = await RunAsync(f, month: 2);

        // Лютий: є лише 301_mass; 304_mass немає - речовина без результату, а не нуль.
        Assert.Equal(0.7m * 10m, Value(output, f.Substance301));
        Assert.Null(Value(output, f.Substance304));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_без_складу_речовин_з_символом_не_дає_результату_і_пише_REF_у_трейсі()
    {
        var f = await ArrangeAsync(withSubstances: false);

        var output = await RunAsync(f, month: 1);

        Assert.DoesNotContain(output.Values, v => v.OutputCode == "Total");
        var steps = output.Trace.Where(s => s.StepCode == "Total").ToList();
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal("#REF", s.TraceJson));
    }

    private static decimal? Value(CalculationOutput output, long substanceEntryId)
        => output.Values.SingleOrDefault(v => v.OutputCode == "Total" && v.SubstanceEntryId == substanceEntryId)?.Value;

    private async Task<CalculationOutput> RunAsync(Fixture f, int month)
    {
        await using var db = sql.CreateContext();

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(f.DocumentId, 202600 + month, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, month, 1), new DateOnly(2026, month, DateTime.DaysInMonth(2026, month))));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            new MethodologyStore(db, constantCap: 1000),
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>(),
            new RegistryStore(db),
            new RegistrySnapshotLoader(db));

        var descriptor = new MethodologyDescriptor(
            f.MethodologyId,
            f.VersionId,
            Code: f.MethodologyCode,
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Full);

        var input = new CalculationInput(
            descriptor,
            f.DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202600 + month),
            SourceRowKey: "R1",
            Arguments:
            [
                new CalculationArgument("X", 10m, null, null),
                new CalculationArgument("Stream", null, "FG", null),
            ]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private sealed record Fixture(
        int MethodologyId, string MethodologyCode, int VersionId, long DocumentId, long Substance301, long Substance304);

    private async Task<Fixture> ArrangeAsync(bool withSubstances)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        // Довідник речовин: код запису E301, число - у полі SUBSTANCE_CODE (EcrCode вимагає літери першою).
        var substances = await AddRegistryAsync(db, $"AIRSUB_{tag}");
        var substanceCode = await AddFieldAsync(db, substances, "SUBSTANCE_CODE", CellDataType.String, 1);
        var e301 = await AddEntryAsync(db, substances.Id, "E301", 1);
        var e304 = await AddEntryAsync(db, substances.Id, "E304", 2);
        await SetAsync(db, e301, substanceCode.Id, CellDataType.String, "301");
        await SetAsync(db, e304, substanceCode.Id, CellDataType.String, "304");

        // Склад: (YEAR, MONTH, STREAM, INDICATOR) -> VALUE.
        var comp = await AddRegistryAsync(db, $"COMP_{tag}");
        var year = await AddFieldAsync(db, comp, "YEAR", CellDataType.Int, 1, required: true);
        var month = await AddFieldAsync(db, comp, "MONTH", CellDataType.Int, 2, required: true);
        var stream = await AddFieldAsync(db, comp, "STREAM", CellDataType.String, 3, required: true);
        var indicator = await AddFieldAsync(db, comp, "INDICATOR", CellDataType.String, 4, required: true);
        var value = await AddFieldAsync(db, comp, "VALUE", CellDataType.Decimal, 5);
        db.RegistryKeyDefs.Add(new RegistryKeyDef(
            comp.Id, EcrCode.Create("PK"), Text("PK"), [year, month, stream, indicator],
            isPrimary: true, ignoreCase: true, 1, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var n = 0;
        async Task CompAsync(int m, string s, string i, decimal v)
        {
            var entry = await AddEntryAsync(db, comp.Id, $"H{m:D2}_{s}_{i}_{++n}", n);
            await SetAsync(db, entry, year.Id, CellDataType.Int, 2026);
            await SetAsync(db, entry, month.Id, CellDataType.Int, m);
            await SetAsync(db, entry, stream.Id, CellDataType.String, s);
            await SetAsync(db, entry, indicator.Id, CellDataType.String, i);
            await SetAsync(db, entry, value.Id, CellDataType.Decimal, v);
        }

        await CompAsync(1, "FG", "301_mass", 0.9m);
        await CompAsync(1, "FG", "304_mass", 0.1m);
        await CompAsync(2, "FG", "301_mass", 0.7m);

        var unitId = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var methodology = new Methodology(EcrCode.Create($"CSS_{tag}"), Text("current substance"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var formula = new MethodologyFormula(
            version.Id, EcrCode.Create("Total"), Mass.Replace("{COMP}", $"COMP_{tag}", StringComparison.Ordinal));
        formula.SetResultType(FormulaResultType.Number);
        formula.SetScope(MethodologyFormulaScope.Substance);
        formula.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(formula);
        db.MethodologyOutputs.Add(new MethodologyOutput(version.Id, EcrCode.Create("Total"), unitId, 1));
        if (withSubstances)
        {
            db.MethodologySubstances.Add(new MethodologySubstance(version.Id, e301, 0));
            db.MethodologySubstances.Add(new MethodologySubstance(version.Id, e304, 1));
        }

        await db.SaveChangesAsync();
        return new Fixture(methodology.Id, $"CSS_{tag}", version.Id, DocumentId: 9100, e301, e304);
    }

    private static async Task<RegistryDef> AddRegistryAsync(EcrDbContext db, string code)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry;
    }

    private static async Task<RegistryFieldDef> AddFieldAsync(
        EcrDbContext db, RegistryDef registry, string code, CellDataType dataType, int ordinal, bool required = false)
    {
        var field = new RegistryFieldDef(registry.Id, EcrCode.Create(code), Text(code), dataType, ordinal);
        field.Update(Text(code), ordinal, required);
        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync();
        return field;
    }

    private static async Task<long> AddEntryAsync(EcrDbContext db, int registryDefId, string code, int ordinal)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code), 1, DateTime.UtcNow);
        entry.SetOrdinal(ordinal);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private static async Task SetAsync(EcrDbContext db, long entryId, int fieldId, CellDataType dataType, object value)
    {
        var registryValue = new RegistryValue(entryId, fieldId);
        registryValue.Set(dataType, value, null);
        db.RegistryValues.Add(registryValue);
        await db.SaveChangesAsync();
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
