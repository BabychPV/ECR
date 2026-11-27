// tests/Ecr.Application.Tests/Registries/RegistrySnapshotPairIndexTests.cs
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// ✎ SGF-flaring (вимір 26.11): індекс пари <c>Lookup</c>-полів знімка (<c>ROW.CASE == кейс &amp;&amp; ROW.COMPONENT == компонент</c>)
/// дає рівно ті записи, які дав би повний перегляд за обома рівностями, у порядку <c>GetEntries</c>; не-Lookup чи те саме поле —
/// індексу немає (<c>null</c>, викликач бере один індекс).
/// </summary>
public sealed class RegistrySnapshotPairIndexTests
{
    private const int Case = 1, Component = 2, Composition = 3;
    private const int CaseField = 31, ComponentField = 32, MolField = 33;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-PERF")]
    public void Пара_полів__ті_самі_записи_що_повний_перегляд_за_обома_рівностями()
    {
        var snapshot = Source().Build(new DateOnly(2099, 1, 1));

        Assert.Equal([301L], snapshot.FindReferencingBoth("COMPOSITION", "CASE", 101, "COMPONENT", 201));
        Assert.Equal([303L, 305L], snapshot.FindReferencingBoth("COMPOSITION", "CASE", 102, "COMPONENT", 201));
        Assert.Empty(snapshot.FindReferencingBoth("COMPOSITION", "CASE", 101, "COMPONENT", 299)!);

        // Перевірка проти повного перегляду: кожна пара (кейс, компонент).
        foreach (var c in (long[])[101, 102])
        {
            foreach (var k in (long[])[201, 202])
            {
                var full = snapshot.GetEntries("COMPOSITION")!
                    .Where(e => snapshot.GetField(e, "CASE").AsNumber() == c && snapshot.GetField(e, "COMPONENT").AsNumber() == k)
                    .ToList();
                Assert.Equal(full, snapshot.FindReferencingBoth("COMPOSITION", "CASE", c, "COMPONENT", k));
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-PERF")]
    public void Не_Lookup_або_те_саме_поле__індексу_немає()
    {
        var snapshot = Source().Build(new DateOnly(2099, 1, 1));

        Assert.Null(snapshot.FindReferencingBoth("COMPOSITION", "CASE", 101, "MOL", 1));
        Assert.Null(snapshot.FindReferencingBoth("COMPOSITION", "CASE", 101, "CASE", 101));
        Assert.Null(snapshot.FindReferencingBoth("NO_SUCH", "CASE", 101, "COMPONENT", 201));
    }

    private static RegistrySnapshotSource Source()
    {
        var @case = Def(Case, "CASE");
        var component = Def(Component, "COMPONENT");
        var composition = Def(Composition, "COMPOSITION");
        composition.AddField(Field(Composition, CaseField, "CASE", CellDataType.Lookup, Case));
        composition.AddField(Field(Composition, ComponentField, "COMPONENT", CellDataType.Lookup, Component));
        composition.AddField(Field(Composition, MolField, "MOL", CellDataType.Decimal, null));

        var entries = new List<RegistryEntry>
        {
            Entry(101, Case), Entry(102, Case), Entry(201, Component), Entry(202, Component),
            Entry(301, Composition), Entry(302, Composition), Entry(303, Composition), Entry(304, Composition), Entry(305, Composition),
        };
        var rows = new (long Id, long Case, long Component)[] { (301, 101, 201), (302, 101, 202), (303, 102, 201), (304, 102, 202), (305, 102, 201) };
        var values = rows.SelectMany(r => new RegistrySnapshotValue[]
        {
            new(r.Id, CaseField, null, null, null, null, r.Case, null, null),
            new(r.Id, ComponentField, null, null, null, null, r.Component, null, null),
            new(r.Id, MolField, 1m, null, null, null, null, null, null),
        }).ToList();

        return new RegistrySnapshotSource([@case, component, composition], [], entries, values);
    }

    private static RegistryDef Def(int id, string code)
    {
        var def = new RegistryDef(EcrCode.Create(code), Text(code), false);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(def, id);
        return def;
    }

    private static RegistryFieldDef Field(int registry, int id, string code, CellDataType type, int? target)
    {
        var field = new RegistryFieldDef(registry, EcrCode.Create(code), Text(code), type, id);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(field, id);
        field.PointTo(target);
        return field;
    }

    private static RegistryEntry Entry(long id, int registry)
    {
        var entry = new RegistryEntry(registry, EcrCode.Create("E" + id), Text("E" + id));
        typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
