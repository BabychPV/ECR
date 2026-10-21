// tests/Ecr.Application.Tests/Documents/VersionMigrationLookupRemapTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Мапа записів Lookup шапки між версіями: повна мапа знімає <c>guardedWithData</c> за зміну довідника поля й дає
/// <c>Remapped</c> (Safe), неповна лишає відмову й додає <c>lookupValuesNotMapped</c>.
/// </summary>
/// <remarks>
/// ⚠ Мутаційні докази (червоніють названі тести): у <c>TryRemapLookup</c> замінити <c>unmappedValues == 0</c> на
/// <c>true</c> — «Неповна…» і «Запис без пари…»; прибрати перевірку <c>UnusableTargets</c> — «Непридатна ціль…»;
/// прибрати <c>outcome.Rows.AddRange(mapped)</c> — «Повна мапа…» (немає що писати); у <c>RefusalsFor</c> прибрати гілку
/// <c>LookupUnmappedValues</c> — «Неповна…».
/// </remarks>
public sealed class VersionMigrationLookupRemapTests
{
    private const int OldRegistry = 10;
    private const int NewRegistry = 20;
    private const int OldField = 501;
    private const int NewField = 601;

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Без_мапи_зміна_довідника_з_даними_лишається_guardedWithData()
    {
        var plan = VersionMigrationPlanner.Plan(Version(1, OldField, OldRegistry), Version(2, NewField, NewRegistry), [], Values(5));

        Assert.Equal(5, plan.GuardedValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Empty(plan.LookupRemaps);
        Assert.Empty(plan.LookupReport);
        Assert.DoesNotContain("lookupValuesNotMapped", plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Повна_мапа_знімає_guardedWithData_і_дає_Remapped()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 3, [2] = 2 },
            mappings: new Dictionary<long, long> { [1] = 101, [2] = 102 });

        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Equal(5, plan.LookupRemappedValues);
        Assert.Equal(
            [(OldField, 1L, 101L), (OldField, 2L, 102L)],
            plan.LookupRemaps.Select(r => (r.HeaderFieldDefId, r.OldEntryId, r.NewEntryId)));
        Assert.All(plan.LookupRemaps, r => Assert.Equal("Permit", r.FieldCode));

        var item = Assert.Single(plan.Items, i => i.Kind == "Remapped");
        Assert.Equal(ChangeClass.Safe, item.ChangeClass);
        Assert.Equal(5, item.Values);
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Modified");
        Assert.Equal(5, plan.TransferredValues);

        var report = Assert.Single(plan.LookupReport);
        Assert.True(report.Applied);
        Assert.Equal((5L, 0L, 2, 0), (report.MappedValues, report.UnmappedValues, report.MappedEntries, report.UnmappedEntries));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Кілька_старих_записів_в_один_новий_це_злиття_і_воно_дозволено()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 1, [2] = 1 },
            mappings: new Dictionary<long, long> { [1] = 101, [2] = 101 });

        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Equal(2, plan.LookupRemaps.Count);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Зайві_пари_мапи_ігноруються()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 4 },
            mappings: new Dictionary<long, long> { [1] = 101, [99] = 199 });

        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        var row = Assert.Single(plan.LookupRemaps);
        Assert.Equal((1L, 101L), (row.OldEntryId, row.NewEntryId));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Неповна_мапа_лишає_guardedWithData_і_додає_lookupValuesNotMapped_без_запису()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 3, [2] = 2 },
            mappings: new Dictionary<long, long> { [1] = 101 });

        var refusals = plan.RefusalsFor(VersionMigrationMode.Safe);
        Assert.Contains("guardedWithData", refusals);
        Assert.Contains("lookupValuesNotMapped", refusals);
        Assert.Equal(5, plan.GuardedValues);
        Assert.Equal(5, plan.LookupGuardedValues);
        Assert.Equal(2, plan.LookupUnmappedValues);
        Assert.Empty(plan.LookupRemaps);
        Assert.Equal(0, plan.LookupRemappedValues);

        var report = Assert.Single(plan.LookupReport);
        Assert.Equal([2L], report.UnmappedEntryIds);
        Assert.Equal((3L, 2L, 1, 1), (report.MappedValues, report.UnmappedValues, report.MappedEntries, report.UnmappedEntries));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Значення_без_запису_ніколи_не_мапиться()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 3 },
            mappings: new Dictionary<long, long> { [1] = 101 },
            withoutEntry: 1);

        Assert.Contains("lookupValuesNotMapped", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Equal(1, plan.LookupUnmappedValues);
        Assert.Empty(plan.LookupRemaps);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Непридатна_ціль_рахується_немапованим_і_називається_у_звіті()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 3 },
            mappings: new Dictionary<long, long> { [1] = 101 },
            unusable: new Dictionary<long, string> { [101] = "inactive" });

        Assert.Contains("lookupValuesNotMapped", plan.RefusalsFor(VersionMigrationMode.Safe));
        var report = Assert.Single(plan.LookupReport);
        Assert.Equal([1L], report.UnmappedEntryIds);
        var bad = Assert.Single(report.TargetNotUsable);
        Assert.Equal((101L, "inactive"), (bad.EntryId, bad.Reason));
        Assert.Empty(plan.LookupRemaps);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Мапа_не_знімає_інший_guarded_того_самого_поля()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 3 },
            mappings: new Dictionary<long, long> { [1] = 101 },
            newRequired: true);

        Assert.Contains(plan.Items, i => i.Kind == "Modified" && i.Field == "IsRequired");
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.DoesNotContain("lookupValuesNotMapped", plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Мапа_без_зміни_довідника_нічого_не_робить()
    {
        var input = Input(new Dictionary<long, long> { [1] = 3 }, new Dictionary<long, long> { [1] = 101 });
        var plan = VersionMigrationPlanner.Plan(
            Version(1, OldField, OldRegistry), Version(2, NewField, OldRegistry), [], Values(3), null, input);

        Assert.Empty(plan.LookupRemaps);
        Assert.Empty(plan.LookupReport);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Режим_Presentation_з_мапою_лишає_structural()
    {
        var plan = Plan(
            used: new Dictionary<long, long> { [1] = 3 },
            mappings: new Dictionary<long, long> { [1] = 101 });

        Assert.Contains("structural", plan.RefusalsFor(VersionMigrationMode.Presentation));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Мапа_без_значень_шапки_не_пише_нічого_і_не_відмовляє()
    {
        var plan = Plan(used: new Dictionary<long, long>(), mappings: new Dictionary<long, long> { [1] = 101 });

        Assert.Empty(plan.LookupRemaps);
        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Validate_приймає_правильну_мапу_і_порожню()
    {
        VersionMigrationLookupRemaps.Validate(null);
        VersionMigrationLookupRemaps.Validate([]);
        VersionMigrationLookupRemaps.Validate([Remap("Permit", (1, 101), (2, 101))]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Validate_відхиляє_колонку_дубль_цілі_дубль_from_порожню_мапу_і_стелі()
    {
        AssertInvalid([new VersionMigrationLookupRemap(new VersionMigrationLookupTarget("column", "Permit"), [new(1, 2)])]);
        AssertInvalid([Remap("Permit", (1, 2)), Remap("Permit", (3, 4))]);
        AssertInvalid([Remap("Permit", (1, 2), (1, 3))]);
        AssertInvalid([new VersionMigrationLookupRemap(new VersionMigrationLookupTarget("header", "Permit"), [])]);
        AssertInvalid([Remap("Permit", (0, 2))]);
        AssertInvalid([Remap(" ", (1, 2))]);
        AssertInvalid([Remap(new string('x', VersionMigrationLookupRemaps.MaxCodeLength + 1), (1, 2))]);
        AssertInvalid([new VersionMigrationLookupRemap(new VersionMigrationLookupTarget("header", "P"),
            [.. Enumerable.Range(1, VersionMigrationLookupRemaps.MaxPairs + 1).Select(i => new VersionMigrationLookupPair(i, i + 10_000))])]);
        AssertInvalid([.. Enumerable.Range(1, VersionMigrationLookupRemaps.MaxTargets + 1).Select(i => Remap($"F{i}", (1, 2)))]);
    }

    private static void AssertInvalid(IReadOnlyList<VersionMigrationLookupRemap> remaps)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => VersionMigrationLookupRemaps.Validate(remaps));
        Assert.Equal("err.ECR-SCHM-0422.migrateLookupRemapInvalid", ex.Details!["messageKey"]);
    }

    private static VersionMigrationLookupRemap Remap(string code, params (long From, long To)[] pairs)
        => new(new VersionMigrationLookupTarget("header", code), [.. pairs.Select(p => new VersionMigrationLookupPair(p.From, p.To))]);

    private static VersionMigrationPlan Plan(
        IReadOnlyDictionary<long, long> used, IReadOnlyDictionary<long, long> mappings,
        long withoutEntry = 0, IReadOnlyDictionary<long, string>? unusable = null,
        bool newRequired = false)
    {
        var values = used.Values.Sum() + withoutEntry;
        return VersionMigrationPlanner.Plan(
            Version(1, OldField, OldRegistry),
            Version(2, NewField, NewRegistry, newRequired),
            [],
            Values(values),
            null,
            Input(used, mappings, withoutEntry, unusable));
    }

    private static VersionMigrationLookupInput Input(
        IReadOnlyDictionary<long, long> used, IReadOnlyDictionary<long, long> mappings,
        long withoutEntry = 0, IReadOnlyDictionary<long, string>? unusable = null)
        => new(new Dictionary<int, LookupRemapField>
        {
            [OldField] = new("Permit", mappings, used, withoutEntry, unusable ?? new Dictionary<long, string>()),
        });

    private static Dictionary<int, long> Values(long count) => new() { [OldField] = count };

    private static TemplateVersionSnapshot Version(
        int versionId, int fieldId, int registryId, bool required = false)
    {
        var field = new HeaderFieldDef(versionId, EcrCode.Create("Permit"), Name("Permit"), 0, CellDataType.Lookup);
        field.SetLookup(registryId);
        field.SetRequired(required);

        typeof(Entity<int>).GetProperty("Id")!.SetValue(field, fieldId);

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: [],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = [field],
        };
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
