// tests/Ecr.Api.Tests/PermitListStoreTopEntriesTests.cs
using System.Diagnostics;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>
/// <see cref="PermitListStore.ListTopActiveEntriesAsync"/> на великому реєстрі (до 20 тис. записів після імпорту НСІ):
/// рівно N перших чинних на реєстр, за (Ordinal, Id), без неактивних і видалених, і без квадратичної роботи.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути корельований COUNT (запис береться, якщо записів перед ним менше за N) - результат той самий,
/// але запит на 20 тис. записів іде ~23 с проти менш як 3 с і падає на межі часу нижче.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitListStoreTopEntriesTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Великий_реєстр_віддає_рівно_N_перших_чинних_за_порядком_показу()
    {
        const int total = 20000;
        int bigId, smallId, emptyId;
        await using (var db = sql.CreateContext())
        {
            var big = new RegistryDef(EcrCode.Create($"TOPB_{_tag}"), Text("big"), isTemporal: false);
            var small = new RegistryDef(EcrCode.Create($"TOPS_{_tag}"), Text("small"), isTemporal: false);
            var empty = new RegistryDef(EcrCode.Create($"TOPE_{_tag}"), Text("empty"), isTemporal: false);
            db.RegistryDefs.AddRange(big, small, empty);
            await db.SaveChangesAsync();
            (bigId, smallId, emptyId) = (big.Id, small.Id, empty.Id);

            // 20 тис. записів одним набірним INSERT (через EF - хвилини): Ordinal = i * 7919 mod total - перестановка (7919 і 20000
            // взаємно прості), тобто порядок у таблиці не збігається з порядком показу; кожен 5-й неактивний, кожен 7-й (решта) видалений.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT dic.RegistryEntry (RegistryDefId, Code, DisplayL10n, Ordinal, IsActive, IsDeleted, CreatedAt, CreatedByUserId, DeletedAt, DeletedByUserId)
                SELECT {0}, 'B' + RIGHT('00000' + CAST(n.i AS varchar(5)), 5), N'{{"en":"B"}}', CAST(n.i * 7919 % {1} AS int),
                       CASE WHEN n.i % 5 = 0 THEN 0 ELSE 1 END,
                       CASE WHEN n.i % 5 <> 0 AND n.i % 7 = 0 THEN 1 ELSE 0 END,
                       '1970-01-01', 1,
                       CASE WHEN n.i % 5 <> 0 AND n.i % 7 = 0 THEN '1970-01-01' END,
                       CASE WHEN n.i % 5 <> 0 AND n.i % 7 = 0 THEN 1 END
                FROM (SELECT TOP ({1}) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS bigint) AS i
                      FROM sys.all_objects a CROSS JOIN sys.all_objects b) n
                """, bigId, total);
            var entries = new List<RegistryEntry>();            foreach (var (code, ordinal) in new[] { ("S3", 3), ("S1", 1), ("S2", 2) })
            {
                var e = new RegistryEntry(smallId, EcrCode.Create(code), Text(code), 1, DateTime.UnixEpoch);
                e.SetOrdinal(ordinal);
                entries.Add(e);
            }

            var off = new RegistryEntry(emptyId, EcrCode.Create("OFF"), Text("OFF"), 1, DateTime.UnixEpoch);
            off.Deactivate();
            entries.Add(off);
            db.RegistryEntries.AddRange(entries);
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var store = new PermitListStore(read);
        await store.ListTopActiveEntriesAsync([bigId], 4, CancellationToken.None); // прогрів EF

        var clock = Stopwatch.StartNew();
        var top = await store.ListTopActiveEntriesAsync([bigId, smallId, emptyId], 4, CancellationToken.None);
        clock.Stop();

        Assert.Equal(2, top.Count);
        Assert.False(top.ContainsKey(emptyId));

        // Очікування обчислюємо незалежно: чинні = не кратні 5 і не кратні 7, порядок (Ordinal, Code збігається з Id-порядком вставки не гарантовано).
        var expected = Enumerable.Range(0, total).Where(i => i % 5 != 0 && i % 7 != 0)
            .Select(i => (Ordinal: (int)((long)i * 7919 % total), Code: $"B{i:D5}"))
            .OrderBy(x => x.Ordinal).Take(4).Select(x => x.Code).ToList();
        Assert.Equal(expected, top[bigId].Select(e => e.Code).ToList());
        Assert.Equal(["S1", "S2", "S3"], top[smallId].Select(e => e.Code));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"TOP-N по 20 тис. записів зайняв {clock.Elapsed.TotalMilliseconds:F0} мс.");
    }
}
