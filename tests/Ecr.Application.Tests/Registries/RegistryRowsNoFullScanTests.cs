// tests/Ecr.Application.Tests/Registries/RegistryRowsNoFullScanTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Rows;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// P1-4: сторінка рядків плаского довідника НЕ читає всі записи й усі текстові значення довідника —
/// лише порт відбору в SQL. Кількість матеріалізованих записів не залежить від розміру довідника.
/// </summary>
/// <remarks>
/// ⛔ Доказ червоного: повернути в <c>HandleAsync</c> шлях через <c>SelectFromAsync</c> для плаского
/// довідника (<c>rows.ListEntriesAsync</c>) — <c>DidNotReceive</c> нижче червоні. Вимір на 20 000 записів
/// (<c>docs/build/perf/registry-rows-2026-10-01.md</c>): сторінка 1346 → 87 мс, пошук 6781 → 377 мс.
/// </remarks>
public sealed class RegistryRowsNoFullScanTests
{
    private const int UserId = 9;
    private const int DefId = 700;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryRowsQuery _rows = Substitute.For<IRegistryRowsQuery>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly RegistryDef _def;

    public RegistryRowsNoFullScanTests()
    {
        _user.UserId.Returns(UserId);
        _def = new RegistryDef(EcrCode.Create("BIG_REG"), Text("BIG"), isTemporal: false);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(_def, DefId);
        _registries.FindDefinitionAsync("BIG_REG", Arg.Any<CancellationToken>()).Returns(_def);
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }.Permission("Registry.View").Build());
        _rows.ReadRowsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryRowsSlice([], new Dictionary<long, DateTime>(), []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Сторінка_без_пошуку__лише_SQL_сторінка__без_читання_всіх_записів_і_значень()
    {
        var page = Enumerable.Range(1, 51).Select(i => Entry(i)).ToList();
        _rows.PageVisibleEntriesAsync(Arg.Any<VisibleEntriesFilter>(), 0, 51, Arg.Any<CancellationToken>())
            .Returns(new VisibleEntryPage(page, 20_000));

        var result = await Handler().HandleAsync(Request(null), default);

        Assert.Equal(50, result.Items.Count);
        Assert.Equal(20_000, result.TotalCount);
        Assert.NotNull(result.NextCursor);
        await _rows.DidNotReceiveWithAnyArgs().ListEntriesAsync(default, default, default);
        await _rows.DidNotReceiveWithAnyArgs().ListFieldValuesAsync(default!, default, default);
        await _rows.DidNotReceiveWithAnyArgs().ListVisibleSlimAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Пошук__значення_шукає_SQL__сторінку_вантажать_лише_за_Id_збігів()
    {
        _rows.ListVisibleSlimAsync(Arg.Any<VisibleEntriesFilter>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 1000).Select(i => new RegistryEntrySlim(i, "C" + i, Text("n" + i))).ToList());
        _rows.ListTextMatchesAsync(Arg.Any<IReadOnlyCollection<int>>(), "zzz", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns([new RegistryRowValue(500, 1, null, "xxZZZxx", null, null, null, null, null)]);
        _rows.PageVisibleEntriesAsync(
                Arg.Is<VisibleEntriesFilter>(f => f.EntryIds != null && f.EntryIds.Count == 1 && f.EntryIds.Contains(500)),
                0, 1, Arg.Any<CancellationToken>())
            .Returns(new VisibleEntryPage([Entry(500)], 1));

        var result = await Handler().HandleAsync(Request("zzz"), default);

        Assert.Equal([500L], result.Items.Select(r => r.Id));
        Assert.Equal(1, result.TotalCount);
        await _rows.DidNotReceiveWithAnyArgs().ListEntriesAsync(default, default, default);
        await _rows.DidNotReceiveWithAnyArgs().ListFieldValuesAsync(default!, default, default);
    }

    private GetRegistryRowsHandler Handler() => new(_registries, _rows, new RegistryResolver(), _access, _user);

    private static RegistryRowsRequest Request(string? search)
        => new("BIG_REG", new DateOnly(2026, 9, 30), null, null, search, new Dictionary<string, string>(), new CursorRequest(50, null));

    private static RegistryEntry Entry(long id)
    {
        var entry = new RegistryEntry(DefId, EcrCode.Create("E" + id), Text("E"));
        typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
