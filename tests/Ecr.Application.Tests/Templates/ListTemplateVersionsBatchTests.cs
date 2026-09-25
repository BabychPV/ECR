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
/// на РІВНІ HTTP: один виклик обробника обслуговує весь перелік шаблонів.
/// </summary>
/// <remarks>
/// ⚠ На відміну від <c>ListMethodologiesBatchTests</c> (`RD-06`), тут
/// свідомо НЕ перевіряється «один виклик сховища на весь пакет»: порт
/// <see cref="ITemplateVersionStore"/> лишився БЕЗ нового методу навмисно
/// (межа файлів задачі не охоплює <c>Ecr.Application/Ports</c> і
/// <c>Ecr.Infrastructure</c> — див. коментар над <c>HandleBatchAsync</c>).
/// Обробник і далі кличе <see cref="ITemplateVersionStore.ListVersionsAsync"/>
/// ПО ОДНОМУ на кожен УНІКАЛЬНИЙ шаблон — це й перевіряється нижче: рівно
/// один виклик НА шаблон, не більше (не N+1 і не 2×N), і жодного зайвого
/// виклику на повторний ідентифікатор.
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

        _store.ListVersionsAsync(First, Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<TemplateVersionSummary>(
                [new TemplateVersionSummary(1, "1.0", TemplateVersionStatus.Published, 0, null, null)],
                null,
                null));

        _store.ListVersionsAsync(Second, Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<TemplateVersionSummary>(
                [new TemplateVersionSummary(2, "2.0", TemplateVersionStatus.Draft, 0, null, null)],
                null,
                null));

        _store.ListVersionsAsync(Missing, Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<TemplateVersionSummary>([], null, null));
    }

    private ListTemplateVersionsHandler Handler() => new(_store, _access, _user);

    /// <summary>
    /// Рівно один виклик сховища НА шаблон — не N+1, і відповідь несе версії
    /// КОЖНОГО шаблону, у порядку ЗАПИТУ.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутаційний доказ: якби `HandleBatchAsync` викликав `ListVersionsAsync`
    /// ще й для переліку шаблонів самого (зайвий похідний запит) — число
    /// викликів на `First`/`Second` зросло б до 2, і перша перевірка впала б.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Рівно_один_виклик_сховища_на_шаблон_і_порядок_відповіді_той_самий_що_й_запиту()
    {
        var result = await Handler().HandleBatchAsync([Second, First], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(Second, result[0].TemplateId);
        Assert.Equal(First, result[1].TemplateId);
        Assert.Equal("2.0", Assert.Single(result[0].Versions).Version);
        Assert.Equal("1.0", Assert.Single(result[1].Versions).Version);

        await _store.Received(1).ListVersionsAsync(First, Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
        await _store.Received(1).ListVersionsAsync(Second, Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Повтор ідентифікатора в запиті звужується до ОДНОГО виклику сховища.</summary>
    /// <remarks>
    /// ⛔ Мутаційний доказ: приберіть `seen.Add(...)`/`continue` у
    /// `HandleBatchAsync` — і `ListVersionsAsync(First, ...)` отримає ТРИ
    /// виклики замість одного, а `Received(1)` нижче впаде.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Повтор_ідентифікатора_у_запиті_дає_ОДИН_запис_відповіді_і_ОДИН_виклик_сховища()
    {
        var result = await Handler().HandleBatchAsync([First, First, First], CancellationToken.None);

        Assert.Single(result);
        await _store.Received(1).ListVersionsAsync(First, Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Невідомий шаблон не кидає 404 — пакетний запит адресує МНОЖИНУ.</summary>
    /// <remarks>
    /// ⚠ На відміну від <see cref="ListTemplateVersionsHandler.HandleAsync"/>
    /// (одиничний запит, де відсутність шаблону — 404), тут відсутність
    /// одного шаблону в множині — це просто порожній перелік версій, а не
    /// відмова всього запиту (той самий патерн, що `ListMethodologiesHandler`,
    /// `RD-06`).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Невідомий_шаблон_не_кидає_404_а_дає_порожній_перелік_версій()
    {
        var result = await Handler().HandleBatchAsync([Missing], CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(Missing, entry.TemplateId);
        Assert.Empty(entry.Versions);
    }

    /// <summary>Порожній перелік ідентифікаторів не звертається до сховища взагалі.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Порожній_перелік_ідентифікаторів_не_звертається_до_сховища()
    {
        var result = await Handler().HandleBatchAsync([], CancellationToken.None);

        Assert.Empty(result);
        await _store.DidNotReceive().ListVersionsAsync(
            Arg.Any<int>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }
}
