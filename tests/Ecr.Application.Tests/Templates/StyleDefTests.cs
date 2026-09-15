using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Директива `docs/build/directive-registry-lookup-and-cell-style.md`,
/// Частина B, PR B1: CRUD стилю, якого раніше не було жодного взагалі
/// (`B.0` директиви — модель <see cref="StyleDef"/> і споживач
/// (<c>StyleMapper.cs</c>, Excel-експорт) уже існували, точки входу не
/// було).
/// </summary>
public sealed class StyleDefTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly IStyleCatalog _styles = Substitute.For<IStyleCatalog>();
    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    // ⚠ Реальний Dictionary, не мок: `StyleDef` заводиться й тримається тут
    // так само, як реальна БД тримала б рядки `cfg.StyleDef` — `AddDefinition`/
    // `FindByCodeAsync` читають/пишуть у нього, замість того щоб мокати кожен
    // окремий виклик руками (`ColumnDefTests._draft` — той самий прийом на
    // рівні агрегата).
    private readonly Dictionary<string, StyleDef> _stored = new(StringComparer.Ordinal);

    public StyleDefTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Permission("Template.View").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);

        _styles.FindByCodeAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.GetValueOrDefault(call.ArgAt<string>(1)));

        _styles.When(x => x.AddDefinition(Arg.Any<StyleDef>()))
            .Do(call => _stored[call.Arg<StyleDef>().Code] = call.Arg<StyleDef>());

        _styles.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<int, StyleDef>)_stored.Values
                .Select((s, i) => (s, i))
                .ToDictionary(p => p.i, p => p.s));
    }

    private static SaveStyleDefCommand Command(
        string? fontName = "Calibri", decimal? fontSize = 11m, bool isBold = false, bool isItalic = false,
        int? foregroundArgb = null, int? backgroundArgb = null, string? borderJson = null,
        byte? horizontalAlign = null, byte? verticalAlign = null, bool wrapText = false, string? numberFormat = null)
        => new(
            fontName, fontSize, isBold, isItalic, foregroundArgb, backgroundArgb, borderJson,
            horizontalAlign, verticalAlign, wrapText, numberFormat);

    private SaveStyleDefHandler Save() => new(_styles, _store, _uow, _access, _user);

    private ListStyleDefsHandler List() => new(_styles, _access, _user);

    [Fact]
    public async Task Новий_стиль_заводиться_і_зберігається()
    {
        var saved = await Save().HandleAsync(1, "Bold1", Command(isBold: true), CancellationToken.None);

        Assert.Equal("Bold1", saved.Code);
        Assert.True(saved.IsBold);
        Assert.Single(_stored);

        _styles.Received(1).AddDefinition(Arg.Any<StyleDef>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Повторний_запис_тим_самим_кодом_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(1, "S1", Command(isBold: false), CancellationToken.None);
        var updated = await Save().HandleAsync(1, "S1", Command(isBold: true, fontName: "Arial"), CancellationToken.None);

        // ⛔ Ідентичність — код (той самий принцип, що колонка/`D2-147`):
        // другий запис тим самим кодом ЗМІНЮЄ наявний стиль, а не заводить
        // другий.
        Assert.Single(_stored);
        Assert.True(updated.IsBold);
        Assert.Equal("Arial", updated.FontName);

        // AddDefinition — лише РАЗ, на створення; другий запис його не кличе.
        _styles.Received(1).AddDefinition(Arg.Any<StyleDef>());
    }

    [Fact]
    public async Task Порожнє_ім_я_шрифту_і_формат_числа_нормалізуються_в_null()
    {
        var saved = await Save().HandleAsync(
            1, "S1", Command(fontName: "   ", numberFormat: ""), CancellationToken.None);

        Assert.Null(saved.FontName);
        Assert.Null(saved.NumberFormat);
    }

    [Theory]
    [InlineData((byte)4, null)]
    [InlineData(null, (byte)3)]
    public async Task Вирівнювання_поза_відомим_StyleMapper_діапазоном_відхиляється(
        byte? horizontalAlign, byte? verticalAlign)
    {
        // ⛔ Мутаційний доказ: `StyleMapper.Horizontal`/`Vertical` (Excel-
        // адаптер) мовчки зводять НЕВІДОМЕ значення до Left/Top — якби
        // `StyleDef.SetAppearance` цього не перевіряв, стиль, який автор
        // шаблону вважає «вирівняно по правому краю (2)», міг би тихо
        // прийняти будь-яке число і чекати сюрпризу лише в експортованій
        // книзі. RED без перевірки в домені — GREEN з нею.
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(
                1, "S1", Command(horizontalAlign: horizontalAlign, verticalAlign: verticalAlign),
                CancellationToken.None));

        Assert.Equal("ECR-CFG-0422", error.ErrorCode);

        // ⚠ НЕ `Assert.Empty(_stored)`: у РЕАЛЬНОМУ EF `AddDefinition` лише
        // СТАВИТЬ сутність на чергу вставки (`db.StyleDefs.Add`, той самий
        // приклад, що й `IRegistryStore.AddDefinition`) — фізичний запис
        // робить `SaveChangesAsync`, до якого виконання тут не доходить
        // узагалі (виняток летить раніше). Справжня властивість, яку варто
        // довести, — САМЕ це: недійсний стиль ніколи не досягає коміту.
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Запис_стилю_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, "S1", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Без_права_Template_Edit_стиль_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, "S1", Command(), CancellationToken.None));

        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Перелік_повертає_усі_стилі_версії_відсортовано_за_кодом()
    {
        await Save().HandleAsync(1, "Zebra", Command(), CancellationToken.None);
        await Save().HandleAsync(1, "Alpha", Command(), CancellationToken.None);

        var list = await List().HandleAsync(1, CancellationToken.None);

        Assert.Equal(["Alpha", "Zebra"], list.Select(s => s.Code));
    }
}
