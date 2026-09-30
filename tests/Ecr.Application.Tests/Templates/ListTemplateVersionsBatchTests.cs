using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// <c>BR-07</c>: перелік шаблонів (<c>/admin/templates</c>, <c>TemplatesPage.
/// tsx</c>) читав версії ОКРЕМИМ HTTP-запитом на КОЖЕН рядок — підтверджений
/// 2026-09-25 пробіл продуктивності (N+1 запитів замість одного).
/// <see cref="ListTemplateVersionsHandler.HandleBatchAsync"/> закриває його
/// і на рівні HTTP, і на рівні SQL.
/// </summary>
/// <remarks>
/// ⚠ Два рівні доказу, як у <c>ListMethodologiesBatchTests</c> (`RD-06`):
/// тут — «один виклик порту на весь пакет» (обробник не повернувся до циклу
/// по <see cref="ITemplateVersionStore.ListVersionsAsync"/>); число самих
/// SQL-команд міряє <c>TemplateVersionStoreBatchQueryCountTests</c>
/// (<c>Ecr.Infrastructure.Tests</c>) на живій базі.
/// </remarks>
public sealed class ListTemplateVersionsBatchTests
{
    private const int First = 10;
    private const int Second = 20;
    private const int Missing = 999;

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ListTemplateVersionsBatchTests()
    {
        _user.UserId.Returns(9);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(ListTemplatesHandler.Permission).Build());

        // ⚠ Сховище віддає словник у «своєму» порядку (First раніше за Second) —
        // порядок відповіді мусить відновити обробник за запитом.
        _store.ListVersionsForTemplatesAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, IReadOnlyList<TemplateVersionSummary>>
            {
                [First] = [new TemplateVersionSummary(1, "1.0", TemplateVersionStatus.Published, 0, null, null)],
                [Second] = [new TemplateVersionSummary(2, "2.0", TemplateVersionStatus.Draft, 0, null, null)],
            });
    }

    private ListTemplateVersionsHandler Handler() => new(_store, _access, _user);

    /// <summary>
    /// ОДИН виклик порту на весь пакет, і відповідь несе версії КОЖНОГО
    /// шаблону, у порядку ЗАПИТУ.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутаційний доказ: поверніть у `HandleBatchAsync` цикл
    /// `ListVersionsAsync` по одному на шаблон — `Received(1)` на пакетному
    /// методі й `DidNotReceive` на одиничному впадуть.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Один_виклик_порту_на_весь_пакет_і_порядок_відповіді_той_самий_що_й_запиту()
    {
        var result = await Handler().HandleBatchAsync([Second, First], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(Second, result[0].TemplateId);
        Assert.Equal(First, result[1].TemplateId);
        Assert.Equal("2.0", Assert.Single(result[0].Versions).Version);
        Assert.Equal("1.0", Assert.Single(result[1].Versions).Version);

        await _store.Received(1).ListVersionsForTemplatesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2 && ids.Contains(First) && ids.Contains(Second)),
            100,
            Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ListVersionsAsync(
            Arg.Any<int>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Повтор ідентифікатора в запиті дає ОДИН запис відповіді й не доходить до порту.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Повтор_ідентифікатора_у_запиті_дає_ОДИН_запис_відповіді_і_ОДИН_ідентифікатор_у_порт()
    {
        var result = await Handler().HandleBatchAsync([First, First, First], CancellationToken.None);

        Assert.Single(result);
        await _store.Received(1).ListVersionsForTemplatesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 1 && ids.Contains(First)),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Невідомий шаблон не кидає 404 — пакетний запит адресує МНОЖИНУ.</summary>
    /// <remarks>
    /// ⚠ На відміну від <see cref="ListTemplateVersionsHandler.HandleAsync"/>
    /// (одиничний запит, де відсутність шаблону — 404), тут відсутність
    /// одного шаблону в множині — це просто порожній перелік версій, а не
    /// відмова всього запиту (той самий патерн, що `ListMethodologiesHandler`,
    /// `RD-06`). Сховище такого шаблону у словник не кладе взагалі.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Невідомий_шаблон_не_кидає_404_а_дає_порожній_перелік_версій()
    {
        var result = await Handler().HandleBatchAsync([Missing, First], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(Missing, result[0].TemplateId);
        Assert.Empty(result[0].Versions);
        Assert.Equal(First, result[1].TemplateId);
    }

    /// <summary>Порожній перелік ідентифікаторів не звертається до сховища взагалі.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Порожній_перелік_ідентифікаторів_не_звертається_до_сховища()
    {
        var result = await Handler().HandleBatchAsync([], CancellationToken.None);

        Assert.Empty(result);
        await _store.DidNotReceive().ListVersionsForTemplatesAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ListVersionsAsync(
            Arg.Any<int>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }
}
