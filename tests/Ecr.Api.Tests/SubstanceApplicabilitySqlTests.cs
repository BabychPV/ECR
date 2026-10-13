// tests/Ecr.Api.Tests/SubstanceApplicabilitySqlTests.cs
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
/// Застосовність речовини на рядок (<c>SUBSTANCE_APPLICABLE</c>, 02b) на РЕАЛЬНОМУ SQL Server: справжні
/// <see cref="MethodologyStore"/> (склад речовин, код з поля <c>SUBSTANCE_CODE</c>, правило категорії),
/// <see cref="RegistryStore"/> і <see cref="RegistrySnapshotLoader"/> (довідник застосовності зі складеним ключем
/// з 4 частин і маркером методології) під справжнім <see cref="GenericCalculationModule"/>.
/// </summary>
/// <remarks>
/// Значень прод-даних немає: довідник і числа синтетичні. Мутаційні докази (лише цей клас): повертати з функції
/// <c>TRUE</c> без пошуку запису — червоніє
/// <see cref="Два_рядки_з_різними_наборами_застосовності_дають_різну_кількість_рядків_результату"/>; не
/// передавати довідник застосовності у знімок (<c>RegistryCodes</c>) — червоніють усі (функція дає <c>#REF</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class SubstanceApplicabilitySqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_рядки_з_різними_наборами_застосовності_дають_різну_кількість_рядків_результату()
    {
        var f = await ArrangeAsync(withMarker: true);

        var first = await RunAsync(f, "S1");
        var second = await RunAsync(f, "S2");
        var third = await RunAsync(f, "S3");

        // S1: 301 і 304 (дві з трьох речовин складу); S2: лише 415; S3 - немає жодного запису.
        Assert.Equal(new[] { f.Substance301, f.Substance304 }.Order(), Substances(first));
        Assert.Equal(new[] { f.Substance415 }, Substances(second));
        Assert.Empty(Substances(third));

        // Пропуск - не помилка: жодного #REF у трейсі, є крок пропуску з кодом речовини.
        Assert.DoesNotContain(first.Trace, s => s.TraceJson is not null && s.TraceJson.Contains('#', StringComparison.Ordinal));
        Assert.Contains(first.Trace, s => s.StepCode == "SubstanceNotApplicable" && s.Expression == "415");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Методологія_без_маркера_у_довіднику_не_фільтрується()
    {
        var f = await ArrangeAsync(withMarker: false);

        // Записи застосовності є, але маркера методології немає (довідник «ще не заповнений»): правило не діє.
        var output = await RunAsync(f, "S1");

        Assert.Equal(
            new[] { f.Substance301, f.Substance304, f.Substance415 }.Order(),
            Substances(output));
    }

    private static List<long> Substances(CalculationOutput output)
        => [.. output.Values.Where(v => v.OutputCode == "Total").Select(v => (long)v.SubstanceEntryId!.Value).Order()];

    private async Task<CalculationOutput> RunAsync(Fixture f, string stream)
    {
        await using var db = sql.CreateContext();

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(f.DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

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
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "R1",
            Arguments:
            [
                new CalculationArgument("X", 10m, null, null),
                new CalculationArgument("Stream", null, stream, null),
            ]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private sealed record Fixture(
        int MethodologyId, string MethodologyCode, int VersionId, long DocumentId,
        long Substance301, long Substance304, long Substance415);

    private async Task<Fixture> ArrangeAsync(bool withMarker)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        // Довідник речовин: код запису E301, число - у полі SUBSTANCE_CODE.
        var substances = await AddRegistryAsync(db, $"AIRSUB_{tag}");
        var substanceCode = await AddFieldAsync(db, substances, "SUBSTANCE_CODE", CellDataType.String, 1);
        var e301 = await AddEntryAsync(db, substances.Id, "E301", 1);
        var e304 = await AddEntryAsync(db, substances.Id, "E304", 2);
        var e415 = await AddEntryAsync(db, substances.Id, "E415", 3);
        await SetAsync(db, e301, substanceCode.Id, CellDataType.String, "301");
        await SetAsync(db, e304, substanceCode.Id, CellDataType.String, "304");
        await SetAsync(db, e415, substanceCode.Id, CellDataType.String, "415");

        var methodologyCode = $"APPL_{tag}";
        var registryCode = $"LAND_APPL_{tag}";

        // Довідник застосовності: (METHODOLOGY, SELECTOR_KIND, SELECTOR_VALUE, SUBSTANCE_CODE) - складений первинний ключ.
        var appl = await AddRegistryAsync(db, registryCode);
        var scope = await AddFieldAsync(db, appl, "METHODOLOGY", CellDataType.String, 1, required: true);
        var kind = await AddFieldAsync(db, appl, "SELECTOR_KIND", CellDataType.String, 2, required: true);
        var value = await AddFieldAsync(db, appl, "SELECTOR_VALUE", CellDataType.String, 3, required: true);
        var code = await AddFieldAsync(db, appl, "SUBSTANCE_CODE", CellDataType.String, 4, required: true);
        db.RegistryKeyDefs.Add(new RegistryKeyDef(
            appl.Id, EcrCode.Create("PK"), Text("PK"), [scope, kind, value, code],
            isPrimary: true, ignoreCase: false, 1, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var n = 0;
        async Task ApplAsync(string s, string k, string v, string c)
        {
            var entry = await AddEntryAsync(db, appl.Id, $"A{++n:D3}", n);
            await SetAsync(db, entry, scope.Id, CellDataType.String, s);
            await SetAsync(db, entry, kind.Id, CellDataType.String, k);
            await SetAsync(db, entry, value.Id, CellDataType.String, v);
            await SetAsync(db, entry, code.Id, CellDataType.String, c);
        }

        if (withMarker)
        {
            await ApplAsync(methodologyCode, "*", "*", "*");
        }

        await ApplAsync(methodologyCode, "Stream", "S1", "301");
        await ApplAsync(methodologyCode, "Stream", "S1", "304");
        await ApplAsync(methodologyCode, "Stream", "S2", "415");

        var unitId = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var methodology = new Methodology(EcrCode.Create(methodologyCode), Text("substance applicability"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var formula = new MethodologyFormula(version.Id, EcrCode.Create("Total"), "@X * 2");
        formula.SetResultType(FormulaResultType.Number);
        formula.SetScope(MethodologyFormulaScope.Substance);
        formula.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(formula);
        db.MethodologyOutputs.Add(new MethodologyOutput(version.Id, EcrCode.Create("Total"), unitId, 1));
        db.MethodologySubstances.Add(new MethodologySubstance(version.Id, e301, 0));
        db.MethodologySubstances.Add(new MethodologySubstance(version.Id, e304, 1));
        db.MethodologySubstances.Add(new MethodologySubstance(version.Id, e415, 2));

        // Правило категорії: ключ - значення селектора; речовина без запису застосовності - порожній ключ (пропуск).
        var rule = version.SetCategoryRule(
            existing: null,
            $"if(SUBSTANCE_APPLICABLE('{registryCode}', '{methodologyCode}', 'Stream', @Stream), @Stream, '')",
            Now);
        db.MethodologyCategoryRules.Add(rule);

        await db.SaveChangesAsync();
        return new Fixture(methodology.Id, methodologyCode, version.Id, DocumentId: 9200, e301, e304, e415);
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
