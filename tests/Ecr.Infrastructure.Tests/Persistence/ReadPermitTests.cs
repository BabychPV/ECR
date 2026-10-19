// tests/Ecr.Infrastructure.Tests/Persistence/ReadPermitTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Читання дозволів (Permits, PM-04): один дозвіл, перелік із фільтрами й сторінками, права.</summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>CanAccess</c>-фільтр і перевірку <c>Denied</c> у <c>ListPermitsHandler</c> - червоніє
/// «Користувач_без_Registry_View…»; у <c>PermitLocator</c> прибрати умову <c>t.HeaderRegistryDefId == entry.RegistryDefId</c> -
/// червоніє «Дозвіл_читається_за_Id…» (чужий запис довідника читався б як дозвіл).
/// </remarks>
[Collection("SqlServer")]
public sealed class ReadPermitTests(SqlServerFixture sql)
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2027, 1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string TypeCode => $"RD{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_фільтрується_за_типом_станом_і_підрядком_та_ділиться_на_сторінки()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var other = $"OT{_tag}";
        await PermitFixtures.PublishTypeAsync(h, other);

        var ids = new List<long>();
        foreach (var (type, number, location) in new[] { (TypeCode, "A-1", "Offshore"), (TypeCode, "A-2", "Onshore"), (other, "B-1", "Offshore") })
        {
            await using var create = await h.NextRequestAsync();
            ids.Add((await create.CreatePermit.HandleAsync(new CreatePermitRequest(type, number, location, null, From, To, null), default)).Id);
        }

        await using var read = await h.NextRequestAsync();
        var all = await read.ListPermits.HandleAsync(new CursorRequest(500), null, null, null, null, default);
        Assert.True(ids.TrueForAll(id => all.Items.Any(i => i.Id == id)));

        var ofType = await read.ListPermits.HandleAsync(new CursorRequest(500), TypeCode, "Draft", null, null, default);
        Assert.Equal(["A-1", "A-2"], ofType.Items.Select(i => i.Number));
        Assert.All(ofType.Items, i => Assert.Equal(("Draft", 2, 0, 0), (i.Status, i.TableCount, i.RowCount, i.BoundSheetCount)));

        var found = await read.ListPermits.HandleAsync(new CursorRequest(500), TypeCode, null, "onshore", null, default);
        Assert.Equal(["A-2"], found.Items.Select(i => i.Number));
        Assert.Empty((await read.ListPermits.HandleAsync(new CursorRequest(500), TypeCode, "Active", null, null, default)).Items);

        // Сторінки за курсором (Id), загальна кількість — після фільтрів.
        var first = await read.ListPermits.HandleAsync(new CursorRequest(1), TypeCode, null, null, null, default);
        Assert.Equal((1, (int?)2, true), (first.Items.Count, first.TotalCount, first.NextCursor is not null));
        var second = await read.ListPermits.HandleAsync(new CursorRequest(1, first.NextCursor), TypeCode, null, null, null, default);
        Assert.Equal("A-2", Assert.Single(second.Items).Number);
        Assert.Null(second.NextCursor);

        var badStatus = await Assert.ThrowsAsync<BusinessRuleException>(() => read.ListPermits.HandleAsync(new CursorRequest(10), null, "Bogus", null, null, default));
        Assert.Equal("err.ECR-PRM-0422.statusUnknown", badStatus.Details!["messageKey"]);
        await Assert.ThrowsAsync<BusinessRuleException>(() => read.ListPermits.HandleAsync(new CursorRequest(501), null, null, null, null, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозвіл_читається_за_Id_а_запис_не_з_шапки_і_невідомий_Id_дають_404()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        await using var create = await h.NextRequestAsync();
        var created = await create.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, "KZ-9", null, null, From, To, null), default);

        await using var read = await h.NextRequestAsync();
        var permit = await read.GetPermit.HandleAsync(created.Id, null, default);
        Assert.Equal((created.Id, "KZ-9", "Draft"), (permit.Id, permit.Number, permit.Status));
        Assert.Equal(created.RowVersion, permit.RowVersion);

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => read.GetPermit.HandleAsync(long.MaxValue, null, default));
        Assert.Equal("err.ECR-PRM-0404.permit", missing.Details!["messageKey"]);

        // Запис таблиці дозволу — не шапка: через цей ендпоінт він не читається.
        var places = await PermitFixtures.TableDefAsync(h, TypeCode, "Places");
        long foreign;
        await using (var db = sql.CreateContext())
        {
            var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(
                places.Id, Ecr.Domain.ValueObjects.EcrCode.Create("X1"), PermitHarness.Text("X"), 0, DateTime.UtcNow);
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            foreign = entry.Id;
        }

        var notHeader = await Assert.ThrowsAsync<NotFoundException>(() => read.GetPermit.HandleAsync(foreign, null, default));
        Assert.Equal("ECR-PRM-0404", notHeader.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Користувач_без_Registry_View_не_читає_а_заборона_на_реєстр_шапки_ховає_дозвіл()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        await using var create = await h.NextRequestAsync();
        var created = await create.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, "KZ-8", null, null, From, To, null), default);

        await using var nobody = await PermitHarness.CreateAsync(sql, ["Document.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => nobody.GetPermit.HandleAsync(created.Id, null, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => nobody.ListPermits.HandleAsync(new CursorRequest(10), null, null, null, null, default));
    }
}
