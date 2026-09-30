// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionStoreBatchQueryCountTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>BR-07</c> на рівні SQL: пакетний перелік версій
/// (<c>GET /api/v1/templates/versions?ids=</c>) — ОДНА SQL-команда версій,
/// хоч би скільки шаблонів у пакеті.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Замір, а не таймінг.</b> Лічильник — <c>Ecr.TestKit.DbCommandCounter</c>
/// (EF-перехоплювач). Тут він доречний: увесь шлях — EF-запити
/// <see cref="TemplateVersionStore"/>, сирих команд немає; права підмінені,
/// тож у число входять рівно звернення за версіями.
/// </para>
/// <para>
/// ⛔ Мутація, що валить тест: повернути в
/// <c>ListTemplateVersionsHandler.HandleBatchAsync</c> цикл
/// <c>ListVersionsAsync</c> по одному на шаблон — для трьох шаблонів
/// лічильник показує 3 замість 1.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionStoreBatchQueryCountTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Три_шаблони_дають_одну_SQL_команду_версій()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var ids = await SeedTemplatesAsync(builder, versionsPerTemplate: [2, 1, 3]);

        var counter = new DbCommandCounter();
        await using var db = CountingContext(counter);
        var handler = Handler(new TemplateVersionStore(db));

        counter.Tally.Reset();
        var result = await handler.HandleBatchAsync(ids, CancellationToken.None);
        var seen = counter.Tally.Snapshot();

        Assert.Equal(1, seen.Total);

        // Нуль запитів — це й «оптимізовано», і «нічого не прочитано»: розрізняє їх
        // лише вміст відповіді.
        Assert.Equal([2, 1, 3], result.Select(r => r.Versions.Count));
    }

    /// <summary>
    /// Контракт ендпоінта не змінився: пакетна відповідь — та сама, що дали б
    /// одиничні <see cref="TemplateVersionStore.ListVersionsAsync"/> у порядку
    /// запиту (без повторів), а невідомий шаблон — порожній перелік.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакетна_відповідь_збігається_з_одиничними_переліками_у_порядку_запиту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var ids = await SeedTemplatesAsync(builder, versionsPerTemplate: [2, 0, 3]);
        const int unknown = int.MaxValue;

        // Порядок запиту навмисно не збігається з порядком Id, і є повтор.
        int[] request = [ids[2], unknown, ids[0], ids[1], ids[2]];

        await using var db = builder.CreateContext();
        var store = new TemplateVersionStore(db);
        var result = await Handler(store).HandleBatchAsync(request, CancellationToken.None);

        Assert.Equal([ids[2], unknown, ids[0], ids[1]], result.Select(r => r.TemplateId));

        foreach (var entry in result)
        {
            var single = await store.ListVersionsAsync(entry.TemplateId, new CursorRequest(100), CancellationToken.None);
            Assert.Equal(single.Items, entry.Versions);
        }

        Assert.Empty(result[1].Versions);
        Assert.Empty(result[3].Versions);
    }

    /// <summary>Ліміт на шаблон застосовується до КОЖНОГО шаблону окремо й бере найменші Id.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ліміт_застосовується_до_кожного_шаблону_окремо()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var ids = await SeedTemplatesAsync(builder, versionsPerTemplate: [3, 1]);

        await using var db = builder.CreateContext();
        var store = new TemplateVersionStore(db);

        var byTemplate = await store.ListVersionsForTemplatesAsync(ids, perTemplateLimit: 2, CancellationToken.None);
        var firstTwo = await store.ListVersionsAsync(ids[0], new CursorRequest(2), CancellationToken.None);

        Assert.Equal(firstTwo.Items, byTemplate[ids[0]]);
        Assert.Single(byTemplate[ids[1]]);
    }

    private static async Task<int[]> SeedTemplatesAsync(TestDocumentBuilder builder, int[] versionsPerTemplate)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ids = new int[versionsPerTemplate.Length];

        await using var db = builder.CreateContext();

        for (var i = 0; i < versionsPerTemplate.Length; i++)
        {
            var template = new Template(
                EcrCode.Create($"BR07_{tag}_{i}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = $"BR-07 {tag} {i}" }),
                1,
                Now);
            db.Templates.Add(template);
            await db.SaveChangesAsync();
            ids[i] = template.Id;

            for (var v = 0; v < versionsPerTemplate[i]; v++)
            {
                db.TemplateVersions.Add(new TemplateVersion(template.Id, $"1.0.{v}", 1, Now));
            }

            await db.SaveChangesAsync();
        }

        return ids;
    }

    private EcrDbContext CountingContext(DbCommandCounter counter)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);

    private static ListTemplateVersionsHandler Handler(ITemplateVersionStore store)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(ListTemplatesHandler.Permission).Build());

        return new ListTemplateVersionsHandler(store, access, user);
    }
}
