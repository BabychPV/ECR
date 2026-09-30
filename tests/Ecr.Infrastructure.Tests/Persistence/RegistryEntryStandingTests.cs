// tests/Ecr.Infrastructure.Tests/Persistence/RegistryEntryStandingTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>C7</c>: <see cref="RegistryStore.FindEntryStandingsAsync"/> на справжньому
/// SQL Server віддає стан обігу кожного запису одним запитом — довідник,
/// активність, видалення, вікно чинності, — а невідомий <c>Id</c> пропускає.
/// </summary>
/// <remarks>
/// ⚠ Предмет — саме проєкція зі схеми: шлях запису комірки <c>Lookup</c>
/// ухвалює рішення за цими п'ятьма полями, і переплутане поле (скажімо,
/// <c>IsActive</c> замість <c>IsDeleted</c>) заглушка в тестах обробника не
/// побачила б.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryStandingTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C7")]
    public async Task Стан_обігу_записів_читається_з_бази_одним_пакетом()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var from = new DateOnly(2026, 1, 15);
        var to = new DateOnly(2026, 2, 1);
        int own, other;
        long active, deleted, inactive, windowed, foreign;

        await using (var db = sql.CreateContext())
        {
            var ownDef = new RegistryDef(EcrCode.Create($"C7A_{tag}"), Text("own"), isTemporal: true);
            var otherDef = new RegistryDef(EcrCode.Create($"C7B_{tag}"), Text("other"), isTemporal: false);
            db.RegistryDefs.AddRange(ownDef, otherDef);
            await db.SaveChangesAsync();
            (own, other) = (ownDef.Id, otherDef.Id);

            var activeEntry = new RegistryEntry(own, EcrCode.Create("ACTIVE"), Text("active"));
            var deletedEntry = new RegistryEntry(own, EcrCode.Create("DELETED"), Text("deleted"));
            deletedEntry.SoftDelete(userId: null, utcNow: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var inactiveEntry = new RegistryEntry(own, EcrCode.Create("INACTIVE"), Text("inactive"));
            var windowedEntry = new RegistryEntry(own, EcrCode.Create("WINDOWED"), Text("windowed"));
            windowedEntry.SetValidity(from, to);
            var foreignEntry = new RegistryEntry(other, EcrCode.Create("FOREIGN"), Text("foreign"));

            db.RegistryEntries.AddRange(activeEntry, deletedEntry, inactiveEntry, windowedEntry, foreignEntry);
            await db.SaveChangesAsync();
            (active, deleted, inactive, windowed, foreign) =
                (activeEntry.Id, deletedEntry.Id, inactiveEntry.Id, windowedEntry.Id, foreignEntry.Id);

            // Вимкнений БЕЗ видалення: доменного методу для цього немає, але
            // такий рядок буває (синхронізація, міграція), і поле має читатися
            // окремо від `IsDeleted`.
            await db.RegistryEntries.Where(e => e.Id == inactive)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false));
        }

        await using var read = sql.CreateContext();
        var standings = (await new RegistryStore(read).FindEntryStandingsAsync(
                [active, deleted, inactive, windowed, foreign, 999_999_999_999L], CancellationToken.None))
            .ToDictionary(s => s.Id);

        Assert.Equal(5, standings.Count);
        Assert.DoesNotContain(999_999_999_999L, standings.Keys);

        Assert.Equal(new RegistryEntryStanding(active, own, true, false, null, null), standings[active]);
        Assert.Equal(new RegistryEntryStanding(deleted, own, false, true, null, null), standings[deleted]);
        Assert.Equal(new RegistryEntryStanding(inactive, own, false, false, null, null), standings[inactive]);
        Assert.Equal(new RegistryEntryStanding(windowed, own, true, false, from, to), standings[windowed]);
        Assert.Equal(other, standings[foreign].RegistryDefId);

        // Вікно — напівінтервал: останній чинний день — 31 січня, 1 лютого вже ні.
        Assert.False(standings[windowed].IsValidOn(from.AddDays(-1)));
        Assert.True(standings[windowed].IsValidOn(new DateOnly(2026, 1, 31)));
        Assert.False(standings[windowed].IsValidOn(to));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C7")]
    public async Task Порожній_набір_не_йде_в_базу()
    {
        await using var db = sql.CreateContext();

        Assert.Empty(await new RegistryStore(db).FindEntryStandingsAsync([], CancellationToken.None));
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
