// tests/Ecr.Infrastructure.Tests/Persistence/PermitHeaderKeyTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// R2 (FEATURE-PERMITS §1c, §6): чернетка дозволу (<c>IsActive = 0</c>) у шапці типу не тримає ключ (тип + номер), а решта
/// довідників поводиться як і раніше (Permits, PM-04).
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>RegistryKeyStore.InactiveEntriesHoldNoKeyAsync</c> повернути <c>false</c> - червоніє
/// «Чернетка_дозволу_не_блокує…» (хибний <c>ECR-REG-4092</c> проти чинного дозволу); (2) у <c>RegistryKeyStore</c> прибрати
/// умову <c>HeaderRegistryDefId</c> (повернути <c>true</c> завжди) - червоніє «Звичайний_довідник_…» (вимкнений запис
/// перестає тримати ключ усюди).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitHeaderKeyTests(SqlServerFixture sql)
{
    private static readonly DateOnly Jan2026 = new(2026, 1, 1);
    private static readonly DateOnly Jan2027 = new(2027, 1, 1);
    private static readonly DateOnly Jun2026 = new(2026, 6, 1);
    private static readonly DateOnly Jun2027 = new(2027, 6, 1);
    private static readonly DateOnly Jan2028 = new(2028, 1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_дозволу_не_блокує_чинний_дозвіл_з_тим_самим_номером_а_активація_чернетки_звіряється()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything.Append("Registry.EditData").ToArray());
        await PermitFixtures.PublishTypeAsync(h, $"R2{_tag}");
        var header = await HeaderAsync(h, $"R2{_tag}");

        // Чинний A; чернетка B: створена в непересічному вікні, потім вимкнена й перенесена на вікно, що перетинає A.
        await WriteAsync(h, header, "A1", "N-1", Jan2026, Jan2027);
        var draft = await WriteAsync(h, header, "B1", "N-1", Jan2027, Jan2028);
        await UpdateAsync(h, header, draft, isActive: false, new ValidityWindow(Jun2026, Jun2027));

        // Чинний C з тим самим номером і вікном, що перетинає ЛИШЕ чернетку B, - дозволений: чернетка ключ не тримає.
        var active = await WriteAsync(h, header, "C1", "N-1", Jan2027, Jan2028);
        await using (var check = sql.CreateContext())
        {
            Assert.False(await check.RegistryEntryKeys.AnyAsync(k => k.RegistryEntryId == draft && k.IsLive));
            Assert.True(await check.RegistryEntryKeys.AnyAsync(k => k.RegistryEntryId == active && k.IsLive));
        }

        // Активація чернетки звіряється з чинними (A і C їй перетинаються) - ECR-REG-4092.
        var conflict = await Assert.ThrowsAsync<BusinessRuleException>(
            () => UpdateAsync(h, header, draft, isActive: true, window: null));
        Assert.Equal("ECR-REG-4092", conflict.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звичайний_довідник_вимкнений_запис_і_далі_тримає_ключ_як_до_PM_04()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything.Append("Registry.EditData").ToArray());
        var registry = await NewRegistryWithKeyAsync();

        await h.Writer.WriteAsync(new RegistryEntryWriteBatch(registry, [Row("E1", "K-1", Jan2026, Jan2027)]), default);
        await using var next = await h.NextRequestAsync();
        var second = await next.Writer.WriteAsync(new RegistryEntryWriteBatch(registry, [Row("E2", "K-1", Jan2027, Jan2028)]), default);
        var secondId = second.Rows.Single().Entry.Id;

        // Вимкнути E2 і зсунути його вікно на перетин з E1: вимкнений запис тримає ключ - відмова ECR-REG-4092.
        await using var move = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => move.Writer.UpdateAsync(
            new RegistryEntryUpdateBatch(registry, [new RegistryEntryUpdate(secondId, new Dictionary<string, object?>()) { IsActive = false, Validity = new ValidityWindow(Jun2026, Jun2027) }]),
            default));
        Assert.Equal("ECR-REG-4092", error.ErrorCode);

        // І вимкнений запис лишається живим тримачем ключа.
        await using var deactivate = await h.NextRequestAsync();
        await deactivate.Writer.UpdateAsync(
            new RegistryEntryUpdateBatch(registry, [new RegistryEntryUpdate(secondId, new Dictionary<string, object?>()) { IsActive = false }]), default);
        await using var check = sql.CreateContext();
        Assert.True(await check.RegistryEntryKeys.AnyAsync(k => k.RegistryEntryId == secondId && k.IsLive));
    }

    private static RegistryEntryWrite Row(string code, string key, DateOnly from, DateOnly to)
        => new(code, new Dictionary<string, object?> { ["K"] = key }) { Validity = new ValidityWindow(from, to) };

    private static async Task<RegistryDef> HeaderAsync(PermitHarness h, string typeCode)
    {
        var type = (await h.Permits.FindByCodeAsync(typeCode, default))!;
        return (await h.Registries.FindDefinitionByIdAsync(type.HeaderRegistryDefId, default))!;
    }

    private static async Task<long> WriteAsync(PermitHarness h, RegistryDef header, string code, string number, DateOnly from, DateOnly to)
    {
        await using var request = await h.NextRequestAsync();
        var result = await request.Writer.WriteAsync(
            new RegistryEntryWriteBatch(header.Id, [new RegistryEntryWrite(code, new Dictionary<string, object?> { ["Permit_Number"] = number }) { Validity = new ValidityWindow(from, to) }]),
            default);
        Assert.True(result.Applied, string.Join(";", result.Errors.Select(e => e.MessageKey)));
        return result.Rows.Single().Entry.Id;
    }

    private static async Task UpdateAsync(PermitHarness h, RegistryDef header, long entryId, bool? isActive, ValidityWindow? window)
    {
        await using var request = await h.NextRequestAsync();
        var result = await request.Writer.UpdateAsync(
            new RegistryEntryUpdateBatch(header.Id, [new RegistryEntryUpdate(entryId, new Dictionary<string, object?>()) { IsActive = isActive, Validity = window }]),
            default);
        Assert.True(result.Applied, string.Join(";", result.Errors.Select(e => e.MessageKey)));
    }

    /// <summary>Довідник не з дозволів: темпоральний, первинний ключ за одним полем.</summary>
    private async Task<int> NewRegistryWithKeyAsync()
    {
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(EcrCode.Create($"R2N_{_tag}"), Text("R2 plain"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("K"), Text("K"), CellDataType.String, 1);
        field.Update(Text("K"), 1, isRequired: true);
        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync();

        db.RegistryKeyDefs.Add(new RegistryKeyDef(registry.Id, EcrCode.Create("PK"), Text("PK"), [field], true, ignoreCase: true, createdByUserId: 0, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return registry.Id;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
