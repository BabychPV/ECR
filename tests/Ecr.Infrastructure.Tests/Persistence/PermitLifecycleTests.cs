// tests/Ecr.Infrastructure.Tests/Persistence/PermitLifecycleTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Життєвий цикл дозволу (Permits, PM-04): активація з перевіркою перетину (ECR-PRM-4092), дострокове завершення, поновлення з
/// копією рядків, права. Годинник гарнесу - 2026-10-18.
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) прибрати <c>PermitLifecycle.ActiveOverlap</c> в <c>ActivatePermitHandler</c> і catch ключа - червоніє
/// «Активація_звіряє_перетин…»; (2) у <c>EndPermitHandler</c> прибрати перевірку <c>Active</c> - червоніє «Завершення…»;
/// (3) у <c>RenewPermitHandler.CopyRowsAsync</c> не мапити сестринські посилання - червоніє «Поновлення_копіює_рядки…»;
/// (4) прибрати <c>RegistryAccess.RequireAsync</c> у <c>PermitLocator</c> - червоніє «Читач_не_активує…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitLifecycleTests(SqlServerFixture sql)
{
    private static readonly DateOnly Y2026 = new(2026, 1, 1);
    private static readonly DateOnly Y2027 = new(2027, 1, 1);
    private static readonly DateOnly Y2028 = new(2028, 1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string TypeCode => $"LC{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Активація_звіряє_перетин_чинних_а_суміжне_вікно_і_чернетки_не_заважають()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        var a = await CreateAsync(h, "LC-1", Y2026, Y2027);
        var overlapping = await CreateAsync(h, "LC-1", new DateOnly(2026, 6, 1), new DateOnly(2027, 6, 1));
        var adjacent = await CreateAsync(h, "LC-1", Y2027, Y2028);

        await using var activate = await h.NextRequestAsync();
        var active = await activate.ActivatePermit.HandleAsync(a.Id, default);
        Assert.Equal(("Active", (int?)75), (active.Status, active.EndsInDays));

        await using var key = sql.CreateContext();
        Assert.True(await key.RegistryEntryKeys.AnyAsync(k => k.RegistryEntryId == a.Id && k.IsLive));

        // Повторна активація — не чернетка.
        await using var again = await h.NextRequestAsync();
        var notDraft = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => again.ActivatePermit.HandleAsync(a.Id, default));
        Assert.Equal("err.ECR-PRM-4096.notDraft", notDraft.Details!["messageKey"]);

        // Перетин із чинним A — 4092; сама чернетка A не заважала створенню (R2).
        await using var clash = await h.NextRequestAsync();
        var overlap = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => clash.ActivatePermit.HandleAsync(overlapping.Id, default));
        Assert.Equal(("ECR-PRM-4092", "err.ECR-PRM-4092.overlap"), (overlap.ErrorCode, overlap.Details!["messageKey"]));
        Assert.Equal(a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), overlap.Details["otherId"]);
        await using var stillDraft = await h.NextRequestAsync();
        Assert.Equal("Draft", (await stillDraft.GetPermit.HandleAsync(overlapping.Id, null, default)).Status);

        // Півінтервал: наступне вікно починається в день, коли A уже не діє, - перетину немає.
        await using var next = await h.NextRequestAsync();
        Assert.Equal("Active", (await next.ActivatePermit.HandleAsync(adjacent.Id, default)).Status);

        // Тепер чернетка «overlapping» перетинає ДВОХ чинних; зміна чинного C на перетин із A теж 4092.
        await using var move = await h.NextRequestAsync();
        var current = await move.GetPermit.HandleAsync(adjacent.Id, null, default);
        var moveError = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => move.UpdatePermit.HandleAsync(
            adjacent.Id, new UpdatePermitRequest(current.RowVersion, "LC-1", null, null, new DateOnly(2026, 12, 1), Y2028, null, null), default));
        Assert.Equal("ECR-PRM-4092", moveError.ErrorCode);

        // Перелік за станом.
        await using var list = await h.NextRequestAsync();
        var actives = await list.ListPermits.HandleAsync(new CursorRequest(500), TypeCode, "Active", null, null, default);
        Assert.Equal([a.Id, adjacent.Id], actives.Items.Select(i => i.Id).Order());
        var drafts = await list.ListPermits.HandleAsync(new CursorRequest(500), TypeCode, "Draft", null, null, default);
        Assert.Equal([overlapping.Id], drafts.Items.Select(i => i.Id));
        var future = await list.ListPermits.HandleAsync(new CursorRequest(500), TypeCode, "Expired", null, new DateOnly(2030, 1, 1), default);
        Assert.Equal(2, future.Items.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Завершення_достроково_лише_чинного_з_причиною_і_датою_у_межах_вікна()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var permit = await CreateAsync(h, "LC-END", Y2026, Y2027);

        // Чернетку не завершують.
        await using var draftEnd = await h.NextRequestAsync();
        var notActive = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => draftEnd.EndPermit.HandleAsync(permit.Id, new EndPermitRequest("достатньо довга причина", new DateOnly(2026, 12, 1)), default));
        Assert.Equal("err.ECR-PRM-4096.notActive", notActive.Details!["messageKey"]);

        await using var activate = await h.NextRequestAsync();
        await activate.ActivatePermit.HandleAsync(permit.Id, default);

        await using var bad = await h.NextRequestAsync();
        var shortReason = await Assert.ThrowsAsync<BusinessRuleException>(
            () => bad.EndPermit.HandleAsync(permit.Id, new EndPermitRequest("коротко", null), default));
        Assert.Equal("err.ECR-PRM-0422.endReasonTooShort", shortReason.Details!["messageKey"]);
        foreach (var date in new[] { Y2026, new DateOnly(2027, 2, 1) })
        {
            var range = await Assert.ThrowsAsync<BusinessRuleException>(
                () => bad.EndPermit.HandleAsync(permit.Id, new EndPermitRequest("достатньо довга причина", date), default));
            Assert.Equal("err.ECR-PRM-0422.endDateOutOfRange", range.Details!["messageKey"]);
        }

        // Завершення на майбутню дату: дозвіл ще чинний, але вікно скорочено і Actual end виставлено.
        await using var end = await h.NextRequestAsync();
        var ended = await end.EndPermit.HandleAsync(permit.Id, new EndPermitRequest("  об'єкт виведено з експлуатації  ", new DateOnly(2026, 12, 1)), default);
        Assert.Equal(("Active", new DateOnly(2026, 12, 1), (DateOnly?)new DateOnly(2026, 12, 1), (int?)44), (ended.Status, ended.ValidTo!.Value, ended.ActualEnd, ended.EndsInDays));

        // Завершення «сьогодні» (за замовчуванням): першого нечинного дня дозвіл уже Expired, а далі не завершується.
        await using var today = await h.NextRequestAsync();
        var expired = await today.EndPermit.HandleAsync(permit.Id, new EndPermitRequest("завершення сьогоднішньою датою", null), default);
        Assert.Equal(("Expired", new DateOnly(2026, 10, 18)), (expired.Status, expired.ValidTo!.Value));
        var twice = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => today.EndPermit.HandleAsync(permit.Id, new EndPermitRequest("повторне завершення дозволу", null), default));
        Assert.Equal("err.ECR-PRM-4096.notActive", twice.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поновлення_копіює_шапку_і_рядки_із_сестринськими_посиланнями_на_нові_рядки_в_одній_чернетці()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PublishSiblingTypeAsync(h);

        var source = await CreateAsync(h, "LC-RN", Y2026, Y2027, "Offshore", new PermitReportingFlags(false, true, false, false));
        var placeA = await AddRowAsync(h, "Places", source.Id, new Dictionary<string, object?> { ["Name"] = "A" });
        var placeB = await AddRowAsync(h, "Places", source.Id, new Dictionary<string, object?> { ["Name"] = "B" });
        await AddRowAsync(h, "Limits", source.Id, new Dictionary<string, object?> { ["Place"] = placeA, ["Annual"] = 10m });
        await AddRowAsync(h, "Limits", source.Id, new Dictionary<string, object?> { ["Place"] = placeB, ["Annual"] = 5.5m });

        // Чернетку не поновлюють.
        await using var draftRenew = await h.NextRequestAsync();
        var notRenewable = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => draftRenew.RenewPermit.HandleAsync(source.Id, null, default));
        Assert.Equal("err.ECR-PRM-4096.notRenewable", notRenewable.Details!["messageKey"]);

        await using var activate = await h.NextRequestAsync();
        await activate.ActivatePermit.HandleAsync(source.Id, default);

        await using var renew = await h.NextRequestAsync();
        var renewed = await renew.RenewPermit.HandleAsync(source.Id, null, default);

        Assert.NotEqual(source.Id, renewed.Id);
        Assert.Equal(("Draft", "LC-RN", "Offshore", Y2027, Y2028), (renewed.Status, renewed.Number, renewed.Location, renewed.ValidFrom!.Value, renewed.ValidTo!.Value));
        Assert.Equal((DateOnly?)null, renewed.IssueDate);
        Assert.True(renewed.ReportingFlags.Form301);
        Assert.Equal((2, 15.5m), (renewed.Tables.Single(t => t.Code == "Limits").RowCount, renewed.Tables.Single(t => t.Code == "Limits").Total));
        Assert.Equal(2, renewed.Tables.Single(t => t.Code == "Places").RowCount);

        // Старий дозвіл не змінився; сестринські посилання нових Limits вказують на НОВІ Places.
        await using var read = await h.NextRequestAsync();
        var old = await read.GetPermit.HandleAsync(source.Id, null, default);
        Assert.Equal(("Active", 2, 2), (old.Status, old.Tables.Single(t => t.Code == "Limits").RowCount, old.Tables.Single(t => t.Code == "Places").RowCount));

        await using var db = sql.CreateContext();
        var placesDef = await PermitFixtures.TableDefAsync(h, TypeCode, "Places");
        var limitsDef = await PermitFixtures.TableDefAsync(h, TypeCode, "Limits");
        var placeField = limitsDef.Fields.Single(f => f.Code == "Place").Id;
        var permitField = limitsDef.Fields.Single(f => f.Code == "PERMIT").Id;
        var newLimitIds = await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryFieldDefId == permitField && v.ValueRefEntryId == renewed.Id).Select(v => v.RegistryEntryId).ToListAsync();
        var targets = await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryFieldDefId == placeField && newLimitIds.Contains(v.RegistryEntryId)).Select(v => v.ValueRefEntryId!.Value).ToListAsync();
        var placesPermitField = placesDef.Fields.Single(f => f.Code == "PERMIT").Id;
        var newPlaces = await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryFieldDefId == placesPermitField && v.ValueRefEntryId == renewed.Id)
            .Select(v => v.RegistryEntryId).ToListAsync();
        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Contains(t, newPlaces));
        Assert.DoesNotContain(placeA, targets);

        // Друге поновлення того самого дозволу — збіг номера з початком (4095).
        await using var second = await h.NextRequestAsync();
        var duplicate = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => second.RenewPermit.HandleAsync(source.Id, null, default));
        Assert.Equal("ECR-PRM-4095", duplicate.ErrorCode);

        // Нова чернетка активується без перетину: її вікно починається там, де закінчується старого.
        await using var activateNew = await h.NextRequestAsync();
        Assert.Equal("Active", (await activateNew.ActivatePermit.HandleAsync(renewed.Id, default)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_не_активує_не_завершує_і_не_поновлює_дозвіл()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var permit = await CreateAsync(h, "LC-RO", Y2026, Y2027);

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.ActivatePermit.HandleAsync(permit.Id, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.EndPermit.HandleAsync(permit.Id, new EndPermitRequest("достатньо довга причина", null), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.RenewPermit.HandleAsync(permit.Id, null, default));

        await using var check = await h.NextRequestAsync();
        Assert.Equal("Draft", (await check.GetPermit.HandleAsync(permit.Id, null, default)).Status);
    }

    private async Task<PermitDto> CreateAsync(
        PermitHarness h, string number, DateOnly from, DateOnly to, string? location = null, PermitReportingFlags? flags = null)
    {
        await using var request = await h.NextRequestAsync();
        return await request.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, number, location, null, from, to, flags), default);
    }

    private async Task<long> AddRowAsync(PermitHarness h, string tableCode, long permitId, Dictionary<string, object?> values)
    {
        var table = await PermitFixtures.TableDefAsync(h, TypeCode, tableCode);
        values["PERMIT"] = permitId;
        var result = await h.Writer.WriteAsync(
            new Ecr.Application.Registries.RegistryEntryWriteBatch(
                table.Id, [new Ecr.Application.Registries.RegistryEntryWrite(string.Empty, values)]),
            default);
        Assert.True(result.Applied, string.Join(";", result.Errors.Select(e => e.MessageKey)));
        return result.Rows.Single().Entry.Id;
    }

    /// <summary>Тип: <c>Places</c> (ключ <c>Name</c>) і <c>Limits</c> (ключ - сестринське посилання <c>Place</c>, число <c>Annual</c>).</summary>
    private async Task PublishSiblingTypeAsync(PermitHarness h)
    {
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(TypeCode, PermitFixtures.Name("Sibling"), "Air"), default);
        PermitTypeColumnDto Column(string code, string dataType, bool key, string? scope = null, string? sibling = null)
            => new(code, PermitFixtures.Name(code), dataType, null, true, key, null, null, null, scope, null, sibling, null, 1);

        var places = new PermitTypeTableDto("Places", PermitFixtures.Name("Places"), 1, [Column("Name", "String", true)], []);
        var limits = new PermitTypeTableDto(
            "Limits", PermitFixtures.Name("Limits"), 2,
            [Column("Place", "Lookup", true, "SamePermitTable", "Places"), Column("Annual", "Decimal", false)], []);

        await using var put = await h.NextRequestAsync();
        var draft = await put.Replace.HandleAsync(
            TypeCode, new ReplacePermitTypeRequest(created.RowVersion, PermitFixtures.Name("Sibling"), "Air", [places, limits], []), default);
        await using var publish = await h.NextRequestAsync();
        await publish.Publish.HandleAsync(TypeCode, new PublishPermitTypeRequest("публікація типу для тестів", draft.RowVersion), default);
    }
}
