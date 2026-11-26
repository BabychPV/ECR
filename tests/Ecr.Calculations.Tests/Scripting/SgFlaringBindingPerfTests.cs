// tests/Ecr.Calculations.Tests/Scripting/SgFlaringBindingPerfTests.cs
using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Flert;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.MethodologyScriptMigration.Flaring;
using Ecr.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// ✎ SGF-flaring (DESIGN-sg-flaring §5.9): вимір згенерованих скриптів факелів на обсязі live — довідники складу в розмірі
/// FLERT (523 потоки, 982 кейси, ~22,6 тис. рядків складу, 49 колонок компонентів), місяць подій INT_SG_EVENTS (320 рядків,
/// legacy 2026: 2 899 подій за 9 місяців) і рік TOX_DAILY (8 стовпців × 2 поїзди × 365 днів = 5 840 рядків). Цілі дизайну:
/// &lt; 10 с на місячний документ, &lt; 2 хв на повний рік. Справжній рушій скриптів; без бази (знімок у пам'яті).
/// </summary>
public sealed class SgFlaringBindingPerfTests(ITestOutputHelper output)
{
    private const int StreamDef = 1, CaseDef = 2, ComponentDef = 3, CompositionDef = 4, LocationDef = 5, PinDef = 6;
    private const int Streams = 523;
    private const int CasesPerStream = 2;   // 1 046 ≈ 982 кейсів FLERT
    private const int RowsPerCase = 23;     // ≈ 22,7 тис. рядків складу
    private const long LocationOnshore = 990_001;

    private static readonly string[] Columns = ["14", "412", "415", "416", "417", "453", "481", "999"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-PERF")]
    public async Task Місяць_подій_INT__320_рядків_на_повному_довіднику_складу()
    {
        var body = string.Join('\n', FlertCompositionSchema.ComponentColumns.Select(c => c.Column).Distinct()
            .SelectMany(c => (string[])[$"output M_{c} : one = row.GCV_{c} * 2;", $"output E_{c} : one = M_{c} * row.TOTAL / 1000;"]));
        var stand = Stand(SgFlaringBinding.Bind(body, SgFlaringConsumer.IntOnshore), (new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31)));
        stand.WithEntry("FLARE_LOCATION", "ONSHORE_HP", LocationOnshore).WithEntry("FLARE_LOCATION", "ONSHORE_LP", LocationOnshore + 1);
        stand.AddTable("INT_SG_EVENTS");

        var random = new Random(2026);
        var inputs = Enumerable.Range(0, 320).Select(i => Input(stand, $"E{i}",
            Date("EVENT_START", new DateTime(2099, 1, 1).AddMinutes(random.Next(0, 31 * 24 * 60))),
            new CalculationArgument("LOCATION", null, null, null, LocationOnshore),
            new CalculationArgument("GAS_COMPOSITION_ID", random.Next(1, Streams + 1), null, null),
            new CalculationArgument("STREAM_TYPE", null, $"Case {random.Next(0, CasesPerStream)}", null),
            new CalculationArgument("TOTAL", 1000m, null, null))).ToList();

        var (elapsed, values) = await RunAsync(stand, inputs);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"INT month: {inputs.Count} rows, {values} output values in {elapsed.TotalMilliseconds:F0} ms ({elapsed.TotalMilliseconds / inputs.Count:F2} ms/row)"));
        Assert.Equal(inputs.Count * FlertCompositionSchema.ComponentColumns.Select(c => c.Column).Distinct().Count() * 2, values);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"INT month took {elapsed} (target < 10 s per monthly document)");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-PERF")]
    public async Task Рік_TOX_DAILY__5840_рядків_на_повному_довіднику_складу()
    {
        // Передформули 481 обох типів — без них генератор відмовляє (тіло читає H2S), як у справжньому Thermaloxidizer.
        var body = "output HSE_TO_SG_481_SOR_H2S : one = 100 - row.GCV_SOR_Methane;\noutput HSE_TO_SG_481_EOR_H2S : one = 100 - row.GCV_EOR_Methane;\n"
                   + string.Join('\n', FlertCompositionSchema.ComponentColumns.Select(c => c.Column).Distinct()
                       .Select(c => $"output S_{c} : one = row.GCV_SOR_{c} + row.GCV_EOR_{c};"));
        var stand = Stand(SgFlaringBinding.Bind(body, SgFlaringConsumer.ToxDaily), (new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31)));
        stand.AddTable("TOX_DAILY");

        var inputs = new List<CalculationInput>();
        for (var day = 0; day < 365; day++)
        {
            foreach (var column in Columns)
            {
                foreach (var train in (string[])["Train 1", "Train 2"])
                {
                    inputs.Add(Input(stand, $"{day}|{train}|{column}",
                        Date("DATE", new DateTime(2099, 1, 1).AddDays(day)),
                        new CalculationArgument("TRAIN", null, train, null),
                        new CalculationArgument("TYPE", null, day % 2 == 0 ? "SOR" : "EOR", null),
                        new CalculationArgument("GAS_COLUMN", null, column, null)));
                }
            }
        }

        var (elapsed, values) = await RunAsync(stand, inputs);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TOX year: {inputs.Count} rows, {values} output values in {elapsed.TotalSeconds:F1} s ({elapsed.TotalMilliseconds / inputs.Count:F2} ms/row)"));
        Assert.Equal(5840, inputs.Count);
        Assert.True(values > 0);
        Assert.True(elapsed < TimeSpan.FromMinutes(2), $"TOX year took {elapsed} (target < 2 min per full-year recalculation)");
    }

    private static async Task<(TimeSpan Elapsed, int Values)> RunAsync(ScriptStand stand, List<CalculationInput> inputs)
    {
        stand.RegistryCache = new RegistrySnapshotCache(registryAsOfUtc: null);
        var binding = await stand.Module.PrepareAsync(
            stand.Methodology, ScriptStand.DocumentId, new PeriodKey(ScriptStand.PeriodValue), stand.RegistryCache, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        var values = 0;
        foreach (var input in inputs)
        {
            try
            {
                values += (await stand.Module.ExecuteAsync(binding, input, CancellationToken.None)).Values.Count;
            }
            catch (DomainException)
            {
                // Відхилений рядок (наприклад, стовпець без закріплення) — частина навантаження, як у прогоні.
            }
        }

        watch.Stop();
        return (watch.Elapsed, values);
    }

    private static CalculationArgument Date(string code, DateTime date) => new(code, null, null, null, null, date);

    private static CalculationInput Input(ScriptStand stand, string rowKey, params CalculationArgument[] arguments)
        => new(stand.Methodology, ScriptStand.DocumentId, stand.Main.InstanceId, new PeriodKey(ScriptStand.PeriodValue), rowKey, arguments);

    private static ScriptStand Stand(string text, (DateOnly, DateOnly) bounds)
    {
        var source = Source();
        var stand = new ScriptStand(text, NumericMode.Legacy) { RegistryLoader = new Loader(source), Bounds = bounds };
        stand.RegistryDefinitions.AddRange(source.Registries);
        return stand;
    }

    /// <summary>Довідники складу в розмірі FLERT + закріплення ТОх; кожен 10-й кейс має другу версію з 2099-01-15.</summary>
    private static RegistrySnapshotSource Source()
    {
        var defs = new List<RegistryDef>();
        var keys = new List<RegistryKeyDef>();
        var entries = new List<RegistryEntry>();
        var values = new List<RegistrySnapshotValue>();
        var fields = new Dictionary<(int, string), int>();

        void Def(int id, string code, bool temporal, params (string Code, CellDataType Type, int? Target)[] list)
        {
            var def = new RegistryDef(EcrCode.Create(code), Text(code), temporal);
            SetId(def, id);
            var ordinal = 0;
            foreach (var (fieldCode, type, target) in list)
            {
                var field = new RegistryFieldDef(id, EcrCode.Create(fieldCode), Text(fieldCode), type, ++ordinal);
                SetId(field, (id * 100) + ordinal);
                if (target is { } t)
                {
                    field.PointTo(t);
                    if (id == CompositionDef && fieldCode == "CASE")
                    {
                        field.ComposeInto(ParentDeletePolicy.Cascade);
                    }
                }

                def.AddField(field);
                fields[(id, fieldCode)] = (id * 100) + ordinal;
            }

            defs.Add(def);
        }

        var next = 100_000L;
        long Entry(int registry, string code, DateOnly? from = null, DateOnly? to = null, params (string Field, object Value)[] list)
        {
            var id = next++;
            var entry = new RegistryEntry(registry, EcrCode.Create(code), Text(code));
            typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);
            entry.SetValidity(from, to);
            entries.Add(entry);
            foreach (var (field, value) in list)
            {
                var f = fields[(registry, field)];
                values.Add(value switch
                {
                    long r => new(id, f, null, null, null, null, r, null, null),
                    bool b => new(id, f, null, null, null, b, null, null, null),
                    string s => new(id, f, null, s, null, null, null, null, null),
                    _ => new(id, f, Convert.ToDecimal(value, CultureInfo.InvariantCulture), null, null, null, null, null, null),
                });
            }

            return id;
        }

        Def(StreamDef, "STREAM", false, ("LEGACY_ID", CellDataType.Int, null), ("IS_ONSHORE", CellDataType.Bool, null));
        Def(CaseDef, "STREAM_CASE", true, ("STREAM", CellDataType.Lookup, StreamDef), ("CASE_NAME", CellDataType.String, null),
            ("T_C", CellDataType.Decimal, null), ("NO_COMPOSITION", CellDataType.Bool, null));
        Def(ComponentDef, "COMPONENT", false);
        Def(CompositionDef, "GAS_COMPOSITION", false, ("CASE", CellDataType.Lookup, CaseDef), ("COMPONENT", CellDataType.Lookup, ComponentDef),
            ("MOL_PCT", CellDataType.Decimal, null));
        Def(LocationDef, "FLARE_LOCATION", false, ("IS_ONSHORE", CellDataType.Bool, null));
        Def(PinDef, "TOX_GAS_STREAM", true, ("COLUMN", CellDataType.String, null), ("TRAIN", CellDataType.String, null),
            ("STREAM", CellDataType.Lookup, StreamDef), ("CASE_SOR", CellDataType.String, null), ("CASE_EOR", CellDataType.String, null));

        var components = FlertCompositionSchema.Components.Select(c => Entry(ComponentDef, c)).ToList();
        var location = new RegistryEntry(LocationDef, EcrCode.Create("ONSHORE_HP"), Text("ONSHORE_HP"));
        typeof(Entity<long>).GetProperty("Id")!.SetValue(location, LocationOnshore);
        entries.Add(location);
        values.Add(new(LocationOnshore, fields[(LocationDef, "IS_ONSHORE")], null, null, null, true, null, null, null));

        var boundary = new DateOnly(2099, 1, 15);
        var caseIndex = 0;
        var streamIds = new List<long>();
        for (var s = 1; s <= Streams; s++)
        {
            var stream = Entry(StreamDef, $"S{s}", null, null, ("LEGACY_ID", s), ("IS_ONSHORE", true));
            streamIds.Add(stream);
            for (var c = 0; c < CasesPerStream; c++)
            {
                var versions = (caseIndex++ % 10 == 0)
                    ? new (DateOnly?, DateOnly?)[] { (null, boundary), (boundary, null) }
                    : [(null, null)];
                foreach (var (from, to) in versions)
                {
                    var @case = Entry(CaseDef, $"C{next}", from, to, ("STREAM", stream), ("CASE_NAME", $"Case {c}"), ("T_C", 30m));
                    for (var r = 0; r < RowsPerCase; r++)
                    {
                        Entry(CompositionDef, $"R{next}", null, null, ("CASE", @case), ("COMPONENT", components[(r * 2 + c) % components.Count]),
                            ("MOL_PCT", 100m / RowsPerCase));
                    }
                }
            }
        }

        // Закріплення ТОх: 7 стовпців × 2 поїзди на перші потоки; «999» — без закріплення (рядок відхиляється, як на live).
        for (var i = 0; i < Columns.Length - 1; i++)
        {
            foreach (var train in (string[])["Train 1", "Train 2"])
            {
                Entry(PinDef, $"P{i}_{train.Replace(" ", string.Empty, StringComparison.Ordinal)}", null, null,
                    ("COLUMN", Columns[i]), ("TRAIN", train), ("STREAM", streamIds[i]), ("CASE_SOR", "Case 0"), ("CASE_EOR", "Case 1"));
            }
        }

        foreach (var (registry, parts) in new[] { (CaseDef, new[] { "STREAM", "CASE_NAME" }), (CompositionDef, ["CASE", "COMPONENT"]), (PinDef, ["COLUMN", "TRAIN"]) })
        {
            var def = defs.Single(d => d.Id == registry);
            var keyFields = parts.Select(p => def.Fields.Single(f => f.Code == p)).ToList();
            foreach (var f in keyFields)
            {
                f.Update(Text(f.Code), f.Ordinal, isRequired: true);
            }

            keys.Add(new RegistryKeyDef(registry, EcrCode.Create("PK"), Text("PK"), keyFields, isPrimary: true, ignoreCase: false, 1, DateTime.UnixEpoch));
        }

        return new RegistrySnapshotSource(defs, keys, entries, values);
    }

    private static void SetId(Entity<int> entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class Loader(RegistrySnapshotSource source) : IRegistrySnapshotLoader
    {
        public Task<IRegistrySnapshot> LoadAsync(
            IReadOnlyCollection<int> registryDefIds, DateOnly businessDate, DateTime? registryAsOfUtc, CancellationToken ct)
            => Task.FromResult(source.Build(businessDate));

        public Task<IRegistrySnapshotSource> LoadSourceAsync(
            IReadOnlyCollection<int> registryDefIds, DateTime? registryAsOfUtc, CancellationToken ct)
            => Task.FromResult<IRegistrySnapshotSource>(source);
    }
}
