// tests/Ecr.Infrastructure.Tests/Persistence/RegistryValuesBatchReadTests.cs
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Аудит P9: знімок полів довідника для <c>REGFIELD</c> і перевірка існування записів — сталим
/// числом звернень і без упору в стелю SQL Server у 2100 параметрів, на РЕАЛЬНОМУ SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Мутаційний доказ (перезбіркою): повернути в <c>RegistryFieldSnapshotLoader</c> запит
/// <c>ListValuesAsync</c> на кожен запис — N=3 / N=60 дає 4 / 61 звернення проти 2 / 2, а
/// N=2500 — 2501 проти 4 → обидва тести знімка червоні.
/// </para>
/// <para>
/// ⚠ <c>RegistryStore.FindExistingEntryIdsAsync</c> (<c>Contains</c> без порцій) аудит P9 назвав
/// ризиком стелі 2100 параметрів. Замір цього не підтвердив: на 2502 id EF 10.0.11 шле ОДНУ
/// команду з ОДНИМ параметром (JSON-масив, <c>OPENJSON</c>). Порції там дали б 3 звернення замість
/// одного, тож код не змінено; <see cref="Перевірка_існування_понад_2100_записів_не_впирається_в_ліміт_параметрів"/> —
/// храповик на цю поведінку (зміна режиму перекладу колекцій у <c>DependencyInjection</c> її зламала б).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryValuesBatchReadTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.8")]
    [Trait("Finding", "P9")]
    public async Task Знімок_полів_читає_значення_сталим_числом_звернень()
    {
        var three = await LoadSnapshotAsync(await SeedAsync(3));
        var sixty = await LoadSnapshotAsync(await SeedAsync(60));

        Assert.True(
            three.Commands == sixty.Commands,
            $"N=3 дав {three.Commands}, N=60 дав {sixty.Commands}.\nN=3:\n{three.Detail}\nN=60:\n{sixty.Detail}");

        // Опис довідника і одне пакетне читання значень.
        Assert.True(sixty.Commands == 2, $"очікувалося 2 звернення, а було {sixty.Commands}:\n{sixty.Detail}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.8")]
    [Trait("Finding", "P9")]
    public async Task Знімок_понад_2100_записів_не_впирається_в_ліміт_параметрів()
    {
        var result = await LoadSnapshotAsync(await SeedAsync(2500));

        // Опис довідника + ⌈2500/1000⌉ порцій значень.
        Assert.True(result.Commands == 4, $"очікувалося 4 звернення, а було {result.Commands}:\n{result.Detail}");
        Assert.True(result.MaxParameters < 2100, result.Detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WR-11")]
    [Trait("Finding", "P9")]
    public async Task Перевірка_існування_понад_2100_записів_не_впирається_в_ліміт_параметрів()
    {
        var seeded = await SeedAsync(2500);
        var asked = seeded.Ids.Concat([-1L, long.MaxValue]).ToList();

        var counter = new DbCommandCounter();
        await using var db = CountingContext(counter);

        counter.Tally.Reset();
        var found = await new RegistryStore(db).FindExistingEntryIdsAsync(asked, CancellationToken.None);
        var seen = counter.Tally.Snapshot();

        // ⚠ Замір (EF 10.0.11): одна команда з ОДНИМ параметром — понад стелю EF сам переходить на
        // JSON-параметр (OPENJSON). Храповик тримає саме це: жодна команда не несе 2100+ параметрів
        // і звернень не більше, ніж дали б порції по 1000.
        Assert.Equal(seeded.Ids.Order(), found.Order());
        Assert.True(seen.Total <= 3, $"очікувалося не більше 3 звернень, а було {seen.Total}:\n{seen.Format()}");
        Assert.True(seen.Categories.Max(c => c.MaxParameters) < 2100, seen.Format());
    }

    private async Task<(int Commands, int MaxParameters, string Detail)> LoadSnapshotAsync(Seeded seeded)
    {
        var counter = new DbCommandCounter();
        await using var db = CountingContext(counter);

        // Кожен запис двічі: повтори не мусять давати зайвих звернень.
        var requests = seeded.Ids
            .SelectMany(id => new[]
            {
                new RegistryFieldRequest(id, seeded.RegistryDefId, "Limit"),
                new RegistryFieldRequest(id, seeded.RegistryDefId, "limit"),
            })
            .ToList();

        counter.Tally.Reset();
        var snapshot = await RegistryFieldSnapshotLoader.LoadAsync(new RegistryStore(db), requests, CancellationToken.None);
        var seen = counter.Tally.Snapshot();

        Assert.NotNull(snapshot);
        Assert.Equal(seeded.Ids.Count, snapshot!.Count);
        for (var i = 0; i < seeded.Ids.Count; i++)
        {
            Assert.Equal(ExpressionValue.Number(i + 0.5m), snapshot[seeded.Ids[i]]["Limit"]);
        }

        return (seen.Total, seen.Categories.Max(c => c.MaxParameters), seen.Format());
    }

    /// <summary>Довідник із полем <c>Limit</c> (Decimal) і <paramref name="count"/> записами, <c>Limit = i + 0.5</c>.</summary>
    private async Task<Seeded> SeedAsync(int count)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"VB_{tag}"), Text("Values batch probe"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var limit = new RegistryFieldDef(registry.Id, EcrCode.Create("Limit"), Text("Limit"), CellDataType.Decimal, 1);
        db.RegistryFieldDefs.Add(limit);
        await db.SaveChangesAsync();

        var entries = Enumerable.Range(0, count)
            .Select(i => new RegistryEntry(registry.Id, EcrCode.Create($"E{i}"), Text($"E{i}")))
            .ToList();
        db.RegistryEntries.AddRange(entries);

        for (var i = 0; i < count; i++)
        {
            var value = new RegistryValue(entries[i], limit.Id);
            value.Set(CellDataType.Decimal, i + 0.5m, unitId: null);
            db.RegistryValues.Add(value);
        }

        await db.SaveChangesAsync();
        return new Seeded(registry.Id, [.. entries.Select(e => e.Id)]);
    }

    private EcrDbContext CountingContext(DbCommandCounter counter)
        => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(counter))
            .Options);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Seeded(int RegistryDefId, IReadOnlyList<long> Ids);
}
