// tests/Ecr.Infrastructure.Tests/Persistence/UpdateDeletePermitTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Зміна шапки (оптимістична конкуренція, закінчений дозвіл) і видалення чернетки з рядками (Permits, PM-04, ECR-PRM-4096).
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) прибрати <c>reader.RequireVersionAsync</c> в <c>UpdatePermitHandler</c> - червоніє
/// «Застарілий_токен…»; (2) прибрати перевірку <c>Expired</c> - червоніє «Закінчений_дозвіл_не_редагується»; (3) прибрати
/// <c>entry.IsActive</c>-перевірки в <c>DeletePermitHandler</c> - червоніє «Видаляється_лише_чернетка…» (чинний дозвіл зникає);
/// (4) прибрати <c>PermissionCheck</c>/<c>RegistryAccess.RequireAsync</c> у <c>PermitLocator</c> - червоніє «Читач_не_змінює…».
/// </remarks>
[Collection("SqlServer")]
public sealed class UpdateDeletePermitTests(SqlServerFixture sql)
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2027, 1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string TypeCode => $"UD{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_шапки_чернетки_оновлює_поля_вікно_і_токен_а_дубль_номера_з_початком_відхиляється()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var permit = await CreateAsync(h, "UD-1");
        var other = await CreateAsync(h, "UD-2");

        await using var update = await h.NextRequestAsync();
        var updated = await update.UpdatePermit.HandleAsync(
            permit.Id,
            new UpdatePermitRequest(permit.RowVersion, " UD-1B ", " New place ", new DateOnly(2025, 12, 1), new DateOnly(2026, 2, 1), new DateOnly(2027, 2, 1), null, new PermitReportingFlags(true, false, false, false)),
            default);

        Assert.Equal(("Draft", "UD-1B", "New place"), (updated.Status, updated.Number, updated.Location));
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2027, 2, 1), new DateOnly(2025, 12, 1)), (updated.ValidFrom, updated.ValidTo, updated.IssueDate));
        Assert.True(updated.ReportingFlags.Form300);
        Assert.NotEqual(permit.RowVersion, updated.RowVersion);

        // Очищення необов'язкових полів: розташування й дата видачі зникають.
        await using var clear = await h.NextRequestAsync();
        var cleared = await clear.UpdatePermit.HandleAsync(
            permit.Id, new UpdatePermitRequest(updated.RowVersion, "UD-1B", null, null, new DateOnly(2026, 2, 1), new DateOnly(2027, 2, 1), null, null), default);
        Assert.Equal(((string?)null, (DateOnly?)null, false), (cleared.Location, cleared.IssueDate, cleared.ReportingFlags.Form300));

        // Дубль: той самий номер і початок, що в іншого дозволу типу.
        await using var duplicate = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => duplicate.UpdatePermit.HandleAsync(
            permit.Id, new UpdatePermitRequest(cleared.RowVersion, "ud-2", null, null, From, To, null, null), default));
        Assert.Equal("ECR-PRM-4095", error.ErrorCode);
        Assert.Equal(other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details!["id"]);

        // Некоректне вікно — 422, нічого не змінено.
        await using var bad = await h.NextRequestAsync();
        var order = await Assert.ThrowsAsync<BusinessRuleException>(() => bad.UpdatePermit.HandleAsync(
            permit.Id, new UpdatePermitRequest(cleared.RowVersion, "UD-1B", null, null, To, From, null, null), default));
        Assert.Equal("err.ECR-PRM-0422.validityOrder", order.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілий_токен_дає_409_4096_changed_і_не_затирає_чужу_правку()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var permit = await CreateAsync(h, "UD-V");

        await using var first = await h.NextRequestAsync();
        await first.UpdatePermit.HandleAsync(
            permit.Id, new UpdatePermitRequest(permit.RowVersion, "UD-V", "First", null, From, To, null, null), default);

        await using var second = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => second.UpdatePermit.HandleAsync(
            permit.Id, new UpdatePermitRequest(permit.RowVersion, "UD-V", "Second", null, From, To, null, null), default));
        Assert.Equal(("ECR-PRM-4096", "err.ECR-PRM-4096.changed"), (error.ErrorCode, error.Details!["messageKey"]));

        await using var check = await h.NextRequestAsync();
        Assert.Equal("First", (await check.GetPermit.HandleAsync(permit.Id, null, default)).Location);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Закінчений_дозвіл_не_редагується_а_чинний_редагується()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        // Годинник гарнесу — 2026-10-18: вікно до 2026-06-01 уже минуло.
        var past = await CreateAsync(h, "UD-PAST", new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 1));
        await using var activate = await h.NextRequestAsync();
        await PermitFixtures.ActivateDirectAsync(activate, TypeCode, past.Id);

        await using var read = await h.NextRequestAsync();
        var expired = await read.GetPermit.HandleAsync(past.Id, null, default);
        Assert.Equal(("Expired", (int?)null), (expired.Status, expired.EndsInDays));

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => read.UpdatePermit.HandleAsync(
            past.Id, new UpdatePermitRequest(expired.RowVersion, "UD-PAST", "x", null, new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 1), null, null), default));
        Assert.Equal("err.ECR-PRM-4096.notEditable", error.Details!["messageKey"]);

        var live = await CreateAsync(h, "UD-LIVE", new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 1));
        await using var activateLive = await h.NextRequestAsync();
        await PermitFixtures.ActivateDirectAsync(activateLive, TypeCode, live.Id);
        await using var edit = await h.NextRequestAsync();
        var active = await edit.GetPermit.HandleAsync(live.Id, null, default);
        Assert.Equal(("Active", (int?)14), (active.Status, active.EndsInDays));
        var changed = await edit.UpdatePermit.HandleAsync(
            live.Id, new UpdatePermitRequest(active.RowVersion, "UD-LIVE", "Edited", null, new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 1), null, null), default);
        Assert.Equal(("Active", "Edited"), (changed.Status, changed.Location));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видаляється_лише_чернетка_разом_із_рядками_а_чинний_дозвіл_лишається()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var draft = await CreateAsync(h, "UD-DEL");
        var rowA = await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", draft.Id, "A", 10m);
        var rowB = await PermitFixtures.AddRowAsync(h, TypeCode, "Places", draft.Id, "P1");

        await using var read = await h.NextRequestAsync();
        var withRows = await read.GetPermit.HandleAsync(draft.Id, null, default);
        Assert.Equal((1, 10m), (withRows.Tables.Single(t => t.Code == "Limits").RowCount, withRows.Tables.Single(t => t.Code == "Limits").Total));
        Assert.Equal(1, withRows.Tables.Single(t => t.Code == "Places").RowCount);

        await using var delete = await h.NextRequestAsync();
        await delete.DeletePermit.HandleAsync(draft.Id, default);

        await using var db = sql.CreateContext();
        var gone = await db.RegistryEntries.AsNoTracking().Where(e => e.Id == draft.Id || e.Id == rowA || e.Id == rowB).ToListAsync();
        Assert.Equal(3, gone.Count);
        Assert.All(gone, e => Assert.True(e.IsDeleted));
        Assert.False(await db.RegistryEntryKeys.AnyAsync(k => (k.RegistryEntryId == draft.Id || k.RegistryEntryId == rowA || k.RegistryEntryId == rowB) && k.IsLive));
        await Assert.ThrowsAsync<NotFoundException>(() => read.GetPermit.HandleAsync(draft.Id, null, default));

        // Номер і початок звільнені: той самий дозвіл заводиться знову.
        await using var again = await h.NextRequestAsync();
        Assert.Equal("Draft", (await again.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, "UD-DEL", null, null, From, To, null), default)).Status);

        // Чинний дозвіл не видаляється.
        var live = await CreateAsync(h, "UD-KEEP");
        await using var activate = await h.NextRequestAsync();
        await PermitFixtures.ActivateDirectAsync(activate, TypeCode, live.Id);
        await using var refuse = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => refuse.DeletePermit.HandleAsync(live.Id, default));
        Assert.Equal(("ECR-PRM-4096", "err.ECR-PRM-4096.notDraft"), (error.ErrorCode, error.Details!["messageKey"]));
        await using var check = sql.CreateContext();
        Assert.False((await check.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == live.Id)).IsDeleted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_не_змінює_і_не_видаляє_дозвіл()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var permit = await CreateAsync(h, "UD-RO");

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.UpdatePermit.HandleAsync(
            permit.Id, new UpdatePermitRequest(permit.RowVersion, "UD-RO", "x", null, From, To, null, null), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.DeletePermit.HandleAsync(permit.Id, default));

        await using var check = await h.NextRequestAsync();
        Assert.Equal(permit.RowVersion, (await check.GetPermit.HandleAsync(permit.Id, null, default)).RowVersion);
    }

    private async Task<PermitDto> CreateAsync(PermitHarness h, string number, DateOnly? from = null, DateOnly? to = null)
    {
        await using var request = await h.NextRequestAsync();
        return await request.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, number, null, null, from ?? From, to ?? To, null), default);
    }
}
