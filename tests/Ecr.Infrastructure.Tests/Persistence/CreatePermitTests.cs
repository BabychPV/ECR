// tests/Ecr.Infrastructure.Tests/Persistence/CreatePermitTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Заведення дозволу чернеткою (Permits, PM-04): запис шапки з <c>IsActive = 0</c>, ключ (тип + номер) чернетки не живий,
/// дубль - збіг номера з початком, права.
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) прибрати <c>entry.Deactivate()</c> у <c>PermitDraftWriter</c> - червоний
/// «Створення_дає_чернетку…» (запис активний, ключ живий); (2) прибрати перевірку <c>twin</c> - червоний
/// «Дубль_номера_з_тим_самим_початком…»; (3) прибрати <c>RegistryAccess.RequireAsync</c> - червоний
/// «Читач_без_Registry_EditData_не_створює_дозвіл».
/// </remarks>
[Collection("SqlServer")]
public sealed class CreatePermitTests(SqlServerFixture sql)
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2027, 1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string TypeCode => $"PRM{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_дає_чернетку_з_неживим_ключем_таблицями_і_нульовими_підсумками()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        await using var create = await h.NextRequestAsync();
        var permit = await create.CreatePermit.HandleAsync(
            new CreatePermitRequest(TypeCode, "  KZ-001  ", " Offshore ", new DateOnly(2025, 12, 20), From, To, new PermitReportingFlags(false, true, false, true)),
            default);

        Assert.Equal(("Draft", "KZ-001", "Offshore", TypeCode), (permit.Status, permit.Number, permit.Location, permit.TypeCode));
        Assert.Equal((From, To, new DateOnly(2025, 12, 20), (int?)null), (permit.ValidFrom, permit.ValidTo, permit.IssueDate, permit.EndsInDays));
        Assert.Equal(new PermitReportingFlags(false, true, false, true), permit.ReportingFlags);
        Assert.Empty(permit.BoundSheets);
        Assert.False(string.IsNullOrEmpty(permit.RowVersion));

        // Таблиці типу: код реєстру, нуль рядків, головний підсумок - перший обов'язковий Decimal (Annual).
        var limits = permit.Tables.Single(t => t.Code == "Limits");
        Assert.Equal(($"PRM_{TypeCode}_Limits", 0, "Annual", (decimal?)0m), (limits.RegistryCode, limits.RowCount, limits.TotalColumnCode, limits.Total));
        var places = permit.Tables.Single(t => t.Code == "Places");
        Assert.Equal((0, (string?)null, (decimal?)null), (places.RowCount, places.TotalColumnCode, places.Total));

        // У базі: запис шапки вимкнений, ключ не живий (R2), код — з послідовності.
        await using var check = sql.CreateContext();
        var entry = await check.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == permit.Id);
        Assert.False(entry.IsActive);
        Assert.False(entry.IsDeleted);
        Assert.StartsWith("E", entry.Code, StringComparison.Ordinal);
        Assert.False(await check.RegistryEntryKeys.AnyAsync(k => k.RegistryEntryId == permit.Id && k.IsLive));
        Assert.True(await check.RegistryEntryKeys.AnyAsync(k => k.RegistryEntryId == permit.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дубль_номера_з_тим_самим_початком_відхиляється_409_а_поновлення_з_іншим_початком_ні()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        await using var first = await h.NextRequestAsync();
        var created = await first.CreatePermit.HandleAsync(Request("KZ-DUP", From, To), default);

        await using var twin = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => twin.CreatePermit.HandleAsync(Request("kz-dup", From, To), default));
        Assert.Equal("ECR-PRM-4095", error.ErrorCode);
        Assert.Equal("err.ECR-PRM-4095.number", error.Details!["messageKey"]);
        Assert.Equal(created.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details["id"]);

        // Той самий номер з іншим початком - наступна версія: чернетки одна одну не блокують навіть при перетині вікон.
        await using var renewal = await h.NextRequestAsync();
        var next = await renewal.CreatePermit.HandleAsync(Request("KZ-DUP", To, new DateOnly(2028, 1, 1)), default);
        await using var overlapping = await h.NextRequestAsync();
        var overlap = await overlapping.CreatePermit.HandleAsync(Request("KZ-DUP", new DateOnly(2026, 6, 1), new DateOnly(2027, 6, 1)), default);
        Assert.Equal(3, new[] { created.Id, next.Id, overlap.Id }.Distinct().Count());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Некоректні_поля_тип_без_публікації_і_невідомий_тип_дають_422_і_404()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var draftType = $"DFT{_tag}";
        await h.Create.HandleAsync(new CreatePermitTypeRequest(draftType, PermitFixtures.Name("Draft only"), "Air"), default);

        await AssertInvalidAsync(h, Request(" ", From, To), "numberRequired");
        await AssertInvalidAsync(h, Request(new string('N', 201), From, To), "numberTooLong");
        await AssertInvalidAsync(h, Request("KZ-L", From, To) with { Location = new string('L', 501) }, "locationTooLong");
        await AssertInvalidAsync(h, Request("KZ-V", To, From), "validityOrder");
        await AssertInvalidAsync(h, Request("KZ-V", From, From), "validityOrder");
        await AssertInvalidAsync(h, Request("KZ-T", From, To) with { TypeCode = draftType }, "typeNotPublished");

        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => h.CreatePermit.HandleAsync(Request("KZ-U", From, To) with { TypeCode = "NOPE" }, default));
        Assert.Equal("err.ECR-PRM-0404.type", unknown.Details!["messageKey"]);

        // Нічого не створено.
        await using var check = sql.CreateContext();
        var type = await check.PermitTypeDefs.AsNoTracking().SingleAsync(t => t.Code == TypeCode);
        Assert.Equal(0, await check.RegistryEntries.CountAsync(e => e.RegistryDefId == type.HeaderRegistryDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_без_Registry_EditData_не_створює_дозвіл()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.CreatePermit.HandleAsync(Request("KZ-R", From, To), default));

        await using var editor = await PermitHarness.CreateAsync(sql, PermitHarness.Editor);
        Assert.Equal("Draft", (await editor.CreatePermit.HandleAsync(Request("KZ-E", From, To), default)).Status);
    }

    private CreatePermitRequest Request(string number, DateOnly from, DateOnly to)
        => new(TypeCode, number, null, null, from, to, null);

    private static async Task AssertInvalidAsync(PermitHarness h, CreatePermitRequest request, string caseKey)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => h.CreatePermit.HandleAsync(request, default));
        Assert.Equal("ECR-PRM-0422", error.ErrorCode);
        Assert.Equal($"err.ECR-PRM-0422.{caseKey}", error.Details!["messageKey"]);
    }
}
