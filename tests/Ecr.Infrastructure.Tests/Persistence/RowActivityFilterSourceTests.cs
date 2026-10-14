// tests/Ecr.Infrastructure.Tests/Persistence/RowActivityFilterSourceTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-425: <see cref="RowActivityFilterSource"/> на справжньому SQL — колонки статусу знаходяться за довідником,
/// а не за кодом колонки; запис «Running» — за кодом, а не за Id.
/// </summary>
/// <remarks>
/// Мутації: шукати колонки за кодом <c>Land_Status</c> замість <c>LookupRegistryDefId</c> — червоний перший тест
/// (колонка зветься інакше); не відкидати <c>IsDeleted</c> запис — червоний третій; не фільтрувати колонки за
/// довідником — червоний перший (чужа Lookup-колонка потрапляє в статус).
/// </remarks>
[Collection("SqlServer")]
public sealed class RowActivityFilterSourceTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-425")]
    public async Task Колонки_статусу_знаходяться_за_довідником_а_не_за_кодом_колонки()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);
        int methodologyId;
        int versionId;
        long runningId;
        int statusColumnId;

        await using (var db = sql.CreateContext())
        {
            var status = new RegistryDef(EcrCode.Create($"ST_{_tag}"), Text("Status"), isTemporal: false);
            var other = new RegistryDef(EcrCode.Create($"OT_{_tag}"), Text("Other"), isTemporal: false);
            db.RegistryDefs.AddRange(status, other);
            await db.SaveChangesAsync();

            var running = new RegistryEntry(status.Id, EcrCode.Create("Running"), Text("Running"), 1, DateTime.UnixEpoch);
            var removed = new RegistryEntry(status.Id, EcrCode.Create("Removed"), Text("Removed"), 2, DateTime.UnixEpoch);
            db.RegistryEntries.AddRange(running, removed);

            // Назва колонки НЕ «Land_Status»: рушій не мусить покладатися на код колонки.
            var statusColumn = new ColumnDef(doc.TableDefId, EcrCode.Create($"Cond_{_tag}"), Text("Cond"), 40, CellDataType.Lookup);
            statusColumn.SetLookup(status.Id);
            var foreign = new ColumnDef(doc.TableDefId, EcrCode.Create($"Fuel_{_tag}"), Text("Fuel"), 41, CellDataType.Lookup);
            foreign.SetLookup(other.Id);
            db.ColumnDefs.AddRange(statusColumn, foreign);

            var methodology = new Methodology(EcrCode.Create($"RA_{_tag}"), Text("Row activity"));
            methodology.SetRowActivityFilter($"ST_{_tag}:Running");
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var version = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            (methodologyId, versionId, runningId, statusColumnId) = (methodology.Id, version.Id, running.Id, statusColumn.Id);
        }

        await using var read = sql.CreateContext();
        var source = new RowActivityFilterSource(read);

        var byMethodology = await source.ResolveForMethodologyAsync(methodologyId, [doc.TableDefId, 999_999], CancellationToken.None);
        var byVersion = await source.ResolveForVersionAsync(versionId, [doc.TableDefId], CancellationToken.None);

        Assert.NotNull(byMethodology);
        Assert.Equal(runningId.ToString(System.Globalization.CultureInfo.InvariantCulture), byMethodology.ActiveEntryIdText);
        Assert.Equal([statusColumnId], byMethodology.ColumnsOf(doc.TableDefId));
        Assert.Empty(byMethodology.ColumnsOf(999_999));
        Assert.NotNull(byVersion);
        Assert.Equal([statusColumnId], byVersion.ColumnsOf(doc.TableDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-425")]
    public async Task Методологія_без_прапорця_дає_null_а_невідомий_довідник_фільтр_не_діє()
    {
        int plainId;
        int brokenId;

        await using (var db = sql.CreateContext())
        {
            var plain = new Methodology(EcrCode.Create($"RP_{_tag}"), Text("Plain"));
            var broken = new Methodology(EcrCode.Create($"RB_{_tag}"), Text("Broken"));
            broken.SetRowActivityFilter($"NO_SUCH_{_tag}:Running");
            db.Methodologies.AddRange(plain, broken);
            await db.SaveChangesAsync();
            (plainId, brokenId) = (plain.Id, broken.Id);
        }

        await using var read = sql.CreateContext();
        var source = new RowActivityFilterSource(read);

        Assert.Null(await source.ResolveForMethodologyAsync(plainId, [1], CancellationToken.None));

        var inert = await source.ResolveForMethodologyAsync(brokenId, [1], CancellationToken.None);
        Assert.NotNull(inert);
        Assert.Null(inert.ActiveEntryIdText);
        Assert.Empty(inert.ColumnsOf(1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-425")]
    public async Task Видалений_запис_Running_не_вважається_активним_фільтр_не_діє()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);
        int methodologyId;

        await using (var db = sql.CreateContext())
        {
            var status = new RegistryDef(EcrCode.Create($"SD_{_tag}"), Text("Status"), isTemporal: false);
            db.RegistryDefs.Add(status);
            await db.SaveChangesAsync();

            var running = new RegistryEntry(status.Id, EcrCode.Create("Running"), Text("Running"), 1, DateTime.UnixEpoch);
            running.SoftDelete();
            db.RegistryEntries.Add(running);

            var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"Cd_{_tag}"), Text("Cond"), 42, CellDataType.Lookup);
            column.SetLookup(status.Id);
            db.ColumnDefs.Add(column);

            var methodology = new Methodology(EcrCode.Create($"RD_{_tag}"), Text("Deleted entry"));
            methodology.SetRowActivityFilter($"SD_{_tag}:Running");
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            methodologyId = methodology.Id;
        }

        await using var read = sql.CreateContext();
        var resolved = await new RowActivityFilterSource(read)
            .ResolveForMethodologyAsync(methodologyId, [doc.TableDefId], CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Null(resolved.ActiveEntryIdText);
        Assert.Empty(resolved.ColumnsOf(doc.TableDefId));
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
