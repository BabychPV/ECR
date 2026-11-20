using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Скрипт версії методології на реальному SQL Server (MS01): колонки <c>Engine</c>,
/// <c>ScriptText</c>, <c>ScriptHash</c>, клон і таблиця <c>calc.MethodologyReadTable</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class MethodologyScriptStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task GetScriptAsync_віддає_текст_хеш_рушій_вид_і_режим()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var hash = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        int scriptId;
        int libraryId;
        int emptyId;
        int shadowId;

        await using (var seed = builder.CreateContext())
        {
            var method = new Methodology(EcrCode.Create($"MS_{tag}"), Name("script probe"));
            var library = new Methodology(EcrCode.Create($"MSL_{tag}"), Name("script library"));
            library.SetKind(MethodologyKind.Library);
            seed.Methodologies.AddRange(method, library);
            await seed.SaveChangesAsync();

            var script = new MethodologyVersion(
                method.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            script.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.Full);
            script.SetScript("rows from T1;", hash);

            var libVersion = new MethodologyVersion(
                library.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            libVersion.SetScript("const k = 1;", hash);

            // Версія без тексту: «скрипта немає».
            var empty = new MethodologyVersion(method.Id, "2.0", CalculationLevel.Configuration, 1, Now);

            // Тіньовий скрипт формульної версії читається теж.
            var shadow = new MethodologyVersion(method.Id, "3.0", CalculationLevel.Configuration, 1, Now);
            shadow.SetScript("var a = 1;", hash);

            seed.MethodologyVersions.AddRange(script, libVersion, empty, shadow);
            await seed.SaveChangesAsync();

            scriptId = script.Id;
            libraryId = libVersion.Id;
            emptyId = empty.Id;
            shadowId = shadow.Id;
        }

        await using var read = builder.CreateContext();
        var store = new MethodologyStore(read);

        var found = await store.GetScriptAsync(scriptId, default);
        Assert.NotNull(found);
        Assert.Equal("rows from T1;", found.Text);
        Assert.Equal(hash, found.Hash);
        Assert.Equal(MethodologyEngine.Script, found.Engine);
        Assert.False(found.IsLibrary);
        Assert.Equal(NumericMode.Strict, found.NumericMode);

        var lib = await store.GetScriptAsync(libraryId, default);
        Assert.NotNull(lib);
        Assert.True(lib.IsLibrary);

        var shadowed = await store.GetScriptAsync(shadowId, default);
        Assert.NotNull(shadowed);
        Assert.Equal(MethodologyEngine.Formulas, shadowed.Engine);

        Assert.Null(await store.GetScriptAsync(emptyId, default));
        Assert.Null(await store.GetScriptAsync(int.MaxValue, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_переносить_рушій_скрипт_хеш_і_таблиці_що_читаються()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var hash = Enumerable.Range(10, 32).Select(i => (byte)i).ToArray();

        int sourceId;
        int methodologyId;

        await using (var seed = builder.CreateContext())
        {
            var method = new Methodology(EcrCode.Create($"MSC_{tag}"), Name("script clone"));
            seed.Methodologies.Add(method);
            await seed.SaveChangesAsync();
            methodologyId = method.Id;

            var source = new MethodologyVersion(
                method.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            source.SetScript("rows from T2;", hash);
            seed.MethodologyVersions.Add(source);
            await seed.SaveChangesAsync();

            sourceId = source.Id;
            seed.MethodologyReadTables.AddRange(
                new MethodologyReadTable(sourceId, $"T1_{tag}"),
                new MethodologyReadTable(sourceId, $"T2_{tag}"));
            await seed.SaveChangesAsync();
        }

        int draftId;
        await using (var write = builder.CreateContext())
        {
            var aggregate = await write.Methodologies.Include(m => m.Versions).FirstAsync(m => m.Id == methodologyId);
            var draft = aggregate.Versions.Single(v => v.Id == sourceId).CloneAsDraft("2.0", 1, Now);
            aggregate.AddVersion(draft);
            draftId = await new MethodologyDraftStore(write).SaveDraftAsync(draft, sourceId, default);
        }

        await using var read = builder.CreateContext();
        var clone = await read.MethodologyVersions.AsNoTracking().FirstAsync(v => v.Id == draftId);

        Assert.Equal(MethodologyEngine.Script, clone.Engine);
        Assert.Equal("rows from T2;", clone.ScriptText);
        Assert.Equal(hash, clone.ScriptHash);

        var tables = await read.MethodologyReadTables.AsNoTracking()
            .Where(t => t.MethodologyVersionId == draftId).Select(t => t.TableCode).OrderBy(c => c).ToListAsync();
        Assert.Equal([$"T1_{tag}", $"T2_{tag}"], tables);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиці_що_читаються_замінюються_а_читачі_шукаються_лише_серед_опублікованих()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var codeA = $"RA_{tag}";
        var codeB = $"RB_{tag}";

        int publishedMethodology;
        int draftMethodology;
        int publishedVersionId;
        int draftVersionId;

        await using (var seed = builder.CreateContext())
        {
            var published = new Methodology(EcrCode.Create($"MSP_{tag}"), Name("reader published"));
            var drafted = new Methodology(EcrCode.Create($"MSD_{tag}"), Name("reader draft"));
            seed.Methodologies.AddRange(published, drafted);
            await seed.SaveChangesAsync();

            var pv = new MethodologyVersion(
                published.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            var dv = new MethodologyVersion(
                drafted.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            seed.MethodologyVersions.AddRange(pv, dv);
            await seed.SaveChangesAsync();

            var aggregate = await seed.Methodologies.Include(m => m.Versions).FirstAsync(m => m.Id == published.Id);
            aggregate.PublishVersion(
                aggregate.Versions.Single(v => v.Id == pv.Id),
                publishedByUserId: 2,
                changeReason: "Первинна публікація",
                effectiveFrom: new DateOnly(2099, 1, 1),
                testsPassed: true,
                Now);
            await seed.SaveChangesAsync();

            publishedMethodology = published.Id;
            draftMethodology = drafted.Id;
            publishedVersionId = pv.Id;
            draftVersionId = dv.Id;
        }

        await using (var write = builder.CreateContext())
        {
            var store = new MethodologyStore(write);

            // Дублікати без урахування регістру зливаються, а не ламають унікальний індекс.
            await store.ReplaceScriptReadTablesAsync(publishedVersionId, [codeA, codeA.ToLowerInvariant()], default);
            await store.ReplaceScriptReadTablesAsync(draftVersionId, [codeA], default);
            await write.SaveChangesAsync();
        }

        await using (var write = builder.CreateContext())
        {
            // Повна заміна: A зникає, B з'являється.
            await new MethodologyStore(write).ReplaceScriptReadTablesAsync(publishedVersionId, [codeB], default);
            await write.SaveChangesAsync();
        }

        await using var read = builder.CreateContext();
        var reading = new MethodologyStore(read);

        var codes = await read.MethodologyReadTables.AsNoTracking()
            .Where(t => t.MethodologyVersionId == publishedVersionId).Select(t => t.TableCode).ToListAsync();
        Assert.Equal([codeB], codes);

        Assert.Equal([publishedMethodology], await reading.GetMethodologyIdsReadingTablesAsync([codeB], default));

        // Чернетка читає A, але вона нічого не рахувала: у відповідь не потрапляє.
        Assert.DoesNotContain(draftMethodology, await reading.GetMethodologyIdsReadingTablesAsync([codeA], default));
        Assert.Empty(await reading.GetMethodologyIdsReadingTablesAsync([], default));
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = value });
}
