// tests/Ecr.Api.Tests/SubstanceApplicabilityLookupArgumentSqlTests.cs
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
/// D-556 (коло 3, C12_03): правило категорії <c>if(SUBSTANCE_APPLICABLE(…, REGFIELD(@Fuel, 'NAME')), …, '')</c> на РЕАЛЬНОМУ
/// SQL Server із справжнім <see cref="CalculationBindingStore"/>: довідник Lookup-колонки аргументу потрапляє в знімок
/// і тоді, коли прив'язка методології НЕАКТИВНА (методологію ще не опубліковано; золотий набір публікації рахується до
/// активації прив'язок).
/// </summary>
/// <remarks>
/// Значень прод-даних немає: довідники й числа синтетичні. Мутаційний доказ (лише цей клас): повернути фільтр
/// <c>binding.IsActive</c> у <c>CalculationBindingStore.ListLookupRegistryIdsAsync</c> — червоніє варіант
/// <c>bindingActive = false</c> (кожен рядок: «no category for this row: #REF»); активна прив'язка лишається зеленою —
/// це і є «працює в C05_04, не працює в C12_03».
/// </remarks>
[Collection("SqlServer")]
public sealed class SubstanceApplicabilityLookupArgumentSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Неактивна_прив_язка_не_ховає_довідник_Lookup_аргументу_від_правила_категорії(bool bindingActive)
    {
        var f = await ArrangeAsync(bindingActive);

        var diesel = await RunAsync(f, f.Diesel);
        var gas = await RunAsync(f, f.Gas);

        // Diesel застосовний до 301 і 304, Gas - лише до 415.
        Assert.Equal(new[] { f.Substance301, f.Substance304 }.Order(), Substances(diesel));
        Assert.Equal(new[] { f.Substance415 }, Substances(gas));
    }

    private static List<long> Substances(CalculationOutput output)
        => [.. output.Values.Where(v => v.OutputCode == "Total").Select(v => (long)v.SubstanceEntryId!.Value).Order()];

    private async Task<CalculationOutput> RunAsync(Fixture f, long fuelEntryId)
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
            new CalculationBindingStore(db),
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
                new CalculationArgument("Fuel", null, null, null, fuelEntryId),
            ]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private sealed record Fixture(
        int MethodologyId, string MethodologyCode, int VersionId, long DocumentId,
        long Substance301, long Substance304, long Substance415, long Diesel, long Gas);

    private async Task<Fixture> ArrangeAsync(bool bindingActive)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var substances = await AddRegistryAsync(db, $"AIRSUB_{tag}");
        var substanceCode = await AddFieldAsync(db, substances, "SUBSTANCE_CODE", CellDataType.String, 1);
        var e301 = await AddEntryAsync(db, substances.Id, "E301", 1);
        var e304 = await AddEntryAsync(db, substances.Id, "E304", 2);
        var e415 = await AddEntryAsync(db, substances.Id, "E415", 3);
        await SetAsync(db, e301, substanceCode.Id, CellDataType.String, "301");
        await SetAsync(db, e304, substanceCode.Id, CellDataType.String, "304");
        await SetAsync(db, e415, substanceCode.Id, CellDataType.String, "415");

        // Довідник пального з Lookup-колонки документа: ім'я запису читає REGFIELD(@Fuel, 'NAME').
        var fuels = await AddRegistryAsync(db, $"FUEL_{tag}");
        var fuelName = await AddFieldAsync(db, fuels, "NAME", CellDataType.String, 1);
        var diesel = await AddEntryAsync(db, fuels.Id, "DIESEL", 1);
        var gas = await AddEntryAsync(db, fuels.Id, "GAS", 2);
        await SetAsync(db, diesel, fuelName.Id, CellDataType.String, "Diesel");
        await SetAsync(db, gas, fuelName.Id, CellDataType.String, "Gas");

        var methodologyCode = $"LKAPPL_{tag}";
        var registryCode = $"LAND_APPL_{tag}";
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

        await ApplAsync(methodologyCode, "*", "*", "*");
        await ApplAsync(methodologyCode, "TypeFuel", "Diesel", "301");
        await ApplAsync(methodologyCode, "TypeFuel", "Diesel", "304");
        await ApplAsync(methodologyCode, "TypeFuel", "Gas", "415");

        var unitId = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var methodology = new Methodology(EcrCode.Create(methodologyCode), Text("lookup argument in applicability"));
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

        var rule = version.SetCategoryRule(
            existing: null,
            $"if(SUBSTANCE_APPLICABLE('{registryCode}', '{methodologyCode}', 'TypeFuel', REGFIELD(@Fuel, 'NAME')), "
            + "REGFIELD(@Fuel, 'NAME'), '')",
            Now);
        db.MethodologyCategoryRules.Add(rule);
        await db.SaveChangesAsync();

        // Таблиця документа: Lookup-колонка Fuel (довідник пального) і колонка результату з прив'язкою методології.
        var template = new Template(EcrCode.Create($"LK{tag}"), Text("LK"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        var templateVersion = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(templateVersion);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(templateVersion.Id, EcrCode.Create($"S{tag}"), Text("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Text("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        var fuelColumn = new ColumnDef(table.Id, EcrCode.Create("Fuel"), Text("Fuel"), 1, CellDataType.Lookup);
        fuelColumn.SetLookup(fuels.Id);
        var result = new ColumnDef(table.Id, EcrCode.Create("Result"), Text("Result"), 2, CellDataType.Decimal);
        db.ColumnDefs.AddRange(fuelColumn, result);
        await db.SaveChangesAsync();

        var binding = new CalculationBinding(table.Id, result.Id, methodology.Id, "Total", "{}");
        binding.Update("{}", bindingActive);
        db.CalculationBindings.Add(binding);
        await db.SaveChangesAsync();

        return new Fixture(methodology.Id, methodologyCode, version.Id, DocumentId: 9300, e301, e304, e415, diesel, gas);
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
