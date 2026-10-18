// tests/Ecr.Infrastructure.Tests/Persistence/PermitRowsVisibilityTests.cs
using Ecr.Application.Common;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Registries.Rows;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Видимість рядків таблиць дозволу (Permits, PM-04): редактор бачить рядки чернетки й закінченого дозволу за
/// <c>parentEntryId</c>, а знімок довідників для формул (<c>REGSUM</c>) - лише чинних дозволів.
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>GetRegistryRowsHandler.VisibleAsync</c> прибрати виняток <c>permitHeaders</c> - червоніє
/// «Рядки_чернетки_і_закінченого…»; (2) у <c>RegistrySnapshot.Create</c> прибрати перевірку <c>IsActive</c> батька
/// (<c>RegistryResolver.IsSelectable</c> → завжди true для шапок) - червоніє «Знімок_REGSUM…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitRowsVisibilityTests(SqlServerFixture sql)
{
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly DateOnly Y2026 = new(2026, 1, 1);
    private static readonly DateOnly Y2027 = new(2027, 1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string TypeCode => $"RV{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядки_чернетки_і_закінченого_дозволу_читаються_за_parentEntryId_а_без_нього_чернетка_прихована()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var code = $"PRM_{TypeCode}_Limits";

        var draft = await CreateAsync(h, "RV-D", Y2026, Y2027);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", draft.Id, "A", 10m);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", draft.Id, "B", 5.5m);

        var expired = await CreateAsync(h, "RV-E", new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 1));
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", expired.Id, "C", 1m);
        await PermitFixtures.ActivateDirectAsync(h, TypeCode, expired.Id);

        await using var read = await h.NextRequestAsync();
        var ofDraft = await read.GetRows.HandleAsync(Request(code, draft.Id), default);
        Assert.Equal(["A", "B"], ofDraft.Items.Select(r => r.Values["Name"].Value).Order());

        var ofExpired = await read.GetRows.HandleAsync(Request(code, expired.Id), default);
        Assert.Equal(["C"], ofExpired.Items.Select(r => r.Values["Name"].Value));

        // Без звуження батьком правило пікера те саме, що й було: рядки чернетки й закінченого не видно.
        var all = await read.GetRows.HandleAsync(Request(code, null), default);
        Assert.Empty(all.Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Знімок_REGSUM_не_бачить_рядків_чернетки_і_бачить_їх_після_активації()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);
        var code = $"PRM_{TypeCode}_Limits";
        var limits = await PermitFixtures.TableDefAsync(h, TypeCode, "Limits");
        var annual = limits.Fields.Single(f => f.Code == "Annual").Id;

        var live = await CreateAsync(h, "RV-L", Y2026, Y2027);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", live.Id, "L", 3m);
        await PermitFixtures.ActivateDirectAsync(h, TypeCode, live.Id);

        var draft = await CreateAsync(h, "RV-N", Y2026, Y2027);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", draft.Id, "A", 10m);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", draft.Id, "B", 5.5m);

        // REGSUM(Limits.Annual) = Σ Annual по рядках, які бачить знімок на дату: поки дозвіл чернетка - лише його 3.
        Assert.Equal(3m, await SumAsync(code, annual));

        // Активація чернетки (шапка IsActive = 1) відкриває її рядки знімку: 3 + 10 + 5.5.
        await using var activate = await h.NextRequestAsync();
        await activate.ActivatePermit.HandleAsync(draft.Id, default);
        Assert.Equal(18.5m, await SumAsync(code, annual));
    }

    private async Task<decimal> SumAsync(string registryCode, int annualFieldId)
    {
        _ = annualFieldId;
        await using var db = sql.CreateContext();
        var def = db.RegistryDefs.Single(d => d.Code == registryCode);
        var snapshot = await new RegistrySnapshotLoader(db).LoadAsync([def.Id], Today, null, CancellationToken.None);
        return (snapshot.GetEntries(registryCode) ?? [])
            .Sum(id => snapshot.GetField(id, "Annual").AsNumber() ?? 0m);
    }

    private static RegistryRowsRequest Request(string code, long? parent)
        => new(code, Today, null, parent, null, new Dictionary<string, string>(), new CursorRequest(100));

    private async Task<PermitDto> CreateAsync(PermitHarness h, string number, DateOnly from, DateOnly to)
    {
        await using var request = await h.NextRequestAsync();
        return await request.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, number, null, null, from, to, null), default);
    }
}
