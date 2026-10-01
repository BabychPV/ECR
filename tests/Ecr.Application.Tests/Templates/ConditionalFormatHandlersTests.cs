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

/// <summary>ФВ-2.6/2.7: серверне збереження правил умовного форматування версії.</summary>
public sealed class ConditionalFormatHandlersTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly IConditionalFormatStore _rules = Substitute.For<IConditionalFormatStore>();
    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TemplateBuilder _builder = new() { TemplateVersionId = 1 };
    private readonly TemplateVersion _draft = new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
    private IReadOnlyList<ConditionalFormatRule>? _saved;

    /// <summary><c>ETag</c> порожнього набору — з нього починається правка версії без правил.</summary>
    private static readonly string Current = $"\"{ConditionalFormatsVersion.Of([])}\"";

    public ConditionalFormatHandlersTests()
    {
        var sheet = _builder.Sheet("Water");
        var table = _builder.Table(sheet, "Main");
        _builder.Column(table, "VOL");
        _draft.AddSheet(sheet);

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Permission("Template.View").Build());
        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.LockVersionForUpdateAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => _draft.Status);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _rules.GetAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _rules.ReplaceAsync(1, Arg.Any<IReadOnlyList<ConditionalFormatRule>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _saved = call.ArgAt<IReadOnlyList<ConditionalFormatRule>>(1);
                return Task.CompletedTask;
            });
    }

    private SaveConditionalFormatsHandler Save() => new(_rules, _store, _uow, _access, _user);

    private static ConditionalFormatRuleDto Rule(
        string op = "gt", string? value = "10", string? valueTo = null, string column = "VOL",
        string? bg = "#ff0000")
        => new(column, op, value, valueTo, bg, null, false);

    [Fact]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Набір_замінюється_а_порядок_у_колонці_береться_з_порядку_запиту()
    {
        var result = await Save().HandleAsync(
            1, [Rule("gt", "10"), Rule("between", "1", "5", bg: null), Rule("empty", null)], Current, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal([1, 2, 3], _saved!.Select(r => r.Ordinal));
        Assert.Equal("between", _saved![1].Operator);
        Assert.Null(_saved[2].Value);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Порожній_список_прибирає_усі_правила()
    {
        var result = await Save().HandleAsync(1, [], Current, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_saved!);
    }

    [Theory]
    [InlineData("bogus", "10", null, "#ff0000", "condFormatOperator")]
    [InlineData("gt", "abc", null, "#ff0000", "condFormatOperand")]
    [InlineData("gt", null, null, "#ff0000", "condFormatOperand")]
    [InlineData("between", "1", null, "#ff0000", "condFormatOperand")]
    [InlineData("gt", "10", null, "red", "condFormatColor")]
    public async Task Невалідне_правило_відхиляється_з_ключем_каталогу(
        string op, string? value, string? valueTo, string? bg, string key)
    {
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, [Rule(op, value, valueTo, bg: bg)], Current, CancellationToken.None));

        Assert.Equal("ECR-CFG-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-CFG-0422." + key, ex.Details!["messageKey"]);
        await _rules.DidNotReceive().ReplaceAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<ConditionalFormatRule>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Невідома_колонка_відхиляється()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, [Rule(column: "NOPE")], Current, CancellationToken.None));

        Assert.Equal("err.ECR-CFG-0422.condFormatColumn", ex.Details!["messageKey"]);
    }

    [Fact]
    public async Task Занадто_великий_набір_відхиляється()
    {
        var many = Enumerable.Range(0, SaveConditionalFormatsHandler.MaxRules + 1).Select(_ => Rule()).ToList();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, many, Current, CancellationToken.None));

        Assert.Equal("err.ECR-CFG-0422.condFormatLimit", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Заморожена_версія_правил_не_приймає()
    {
        _draft.Publish(1, Now);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, [Rule()], Current, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);
        await _rules.DidNotReceive().ReplaceAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<ConditionalFormatRule>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Без_права_Template_Edit_запис_відхиляється()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAnyAsync<Exception>(() => Save().HandleAsync(1, [Rule()], Current, CancellationToken.None));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Читання_повертає_збережені_правила()
    {
        _rules.GetAsync(1, Arg.Any<CancellationToken>()).Returns(
            [new ConditionalFormatRule(1, "VOL", 1, "ge", "5", null, "#00ff00", null, true)]);

        var list = await new GetConditionalFormatsHandler(_rules, _store, _access, _user)
            .HandleAsync(1, CancellationToken.None);

        var single = Assert.Single(list);
        Assert.Equal(("VOL", "ge", "5", true), (single.ColumnCode, single.Operator, single.Value, single.IsBold));
    }

    [Fact]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Без_If_Match_набір_не_замінюється()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, [Rule()], null, CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.condFormatIfMatch", ex.Details!["messageKey"]);
        await _rules.DidNotReceive().ReplaceAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<ConditionalFormatRule>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Застаріла_версія_набору_дає_конфлікт_з_актуальною_версією()
    {
        // Хтось зберіг правило після того, як ця правка прочитала порожній набір.
        var theirs = new ConditionalFormatRule(1, "VOL", 1, "lt", "0", null, "#0000ff", null, false);
        _rules.GetAsync(1, Arg.Any<CancellationToken>()).Returns([theirs]);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Save().HandleAsync(1, [Rule()], Current, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0409.condFormatChanged", ex.Details!["messageKey"]);
        Assert.Equal(
            ConditionalFormatsVersion.Of([new ConditionalFormatRuleDto("VOL", "lt", "0", null, "#0000ff", null, false)]),
            ex.Details["version"]);
        await _rules.DidNotReceive().ReplaceAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<ConditionalFormatRule>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Версія_звіряється_під_блокуванням_версії_шаблону()
    {
        var theirs = new ConditionalFormatRule(1, "VOL", 1, "lt", "0", null, "#0000ff", null, false);
        var read = ConditionalFormatsVersion.Of([ConditionalFormatMapperFor(theirs)]);
        _rules.GetAsync(1, Arg.Any<CancellationToken>()).Returns([theirs]);

        // ⚠ Слабкий `ETag` і регістр hex — та сама версія (`NormalizeETag`).
        await Save().HandleAsync(1, [Rule()], $"W/\"{read.ToLowerInvariant()}\"", CancellationToken.None);

        // ⚠ `Arg.Any<int>()`: блокується `version.Id` сутності, а в фікстурі вона не збережена (Id = 0).
        Received.InOrder(() =>
        {
            _store.LockVersionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            _rules.GetAsync(1, Arg.Any<CancellationToken>());
            _rules.ReplaceAsync(1, Arg.Any<IReadOnlyList<ConditionalFormatRule>>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void Версія_залежить_від_порядку_в_колонці_але_не_між_колонками()
    {
        var a1 = new ConditionalFormatRuleDto("A", "gt", "1", null, "#ff0000", null, false);
        var a2 = new ConditionalFormatRuleDto("A", "lt", "0", null, "#0000ff", null, false);
        var b1 = new ConditionalFormatRuleDto("B", "empty", null, null, null, null, true);

        // Між колонками — той самий набір: GET сортує за колонкою, PUT повертає як у запиті.
        Assert.Equal(ConditionalFormatsVersion.Of([a1, b1, a2]), ConditionalFormatsVersion.Of([b1, a1, a2]));

        // Усередині колонки порядок — пріоритет правил, тобто інший набір.
        Assert.NotEqual(ConditionalFormatsVersion.Of([a1, a2]), ConditionalFormatsVersion.Of([a2, a1]));

        // Кожне поле правила — частина версії.
        Assert.NotEqual(ConditionalFormatsVersion.Of([a1]), ConditionalFormatsVersion.Of([a1 with { IsBold = true }]));
        Assert.NotEqual(ConditionalFormatsVersion.Of([a1]), ConditionalFormatsVersion.Of([a1 with { ForegroundHex = "#000000" }]));
        Assert.NotEqual(ConditionalFormatsVersion.Of([a1]), ConditionalFormatsVersion.Of([a1 with { ValueTo = "5" }]));
    }

    private static ConditionalFormatRuleDto ConditionalFormatMapperFor(ConditionalFormatRule r)
        => new(r.ColumnCode, r.Operator, r.Value, r.ValueTo, r.BackgroundHex, r.ForegroundHex, r.IsBold);
}
