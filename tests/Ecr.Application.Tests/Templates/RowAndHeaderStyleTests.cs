using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// CELL-STYLE L2: стиль рядка шаблону (<c>RowDef.StyleId</c>) і шапки таблиці (<c>TableDef.HeaderStyleId</c>) —
/// презентаційні поля: PUT рядка, класифікація, належність стилю версії.
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>existing.SetStyle(command.StyleId)</c> у <c>SaveRowDefHandler</c> — тести запису червоні;
/// прибрати перевірку <c>ofVersion.ContainsKey</c> — тест чужого стилю червоний; прибрати <c>nameof(...StyleId)</c> з
/// <c>PresentationFields</c> — не червоніє через збіг імені з <c>ColumnDef.StyleId</c> (класифікація за назвою поля), тому
/// клас перевіряється окремими тестами за сутностями.
/// </remarks>
public sealed class RowAndHeaderStyleTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IStyleCatalog _styles = Substitute.For<IStyleCatalog>();

    private readonly TemplateBuilder _builder = new() { TemplateVersionId = 1 };
    private readonly TemplateVersion _draft = new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
    private readonly TableDef _table;

    public RowAndHeaderStyleTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var sheet = _builder.Sheet("Water");
        _table = _builder.Table(sheet, "Main", rowMode: TableRowMode.Fixed);
        _draft.AddSheet(sheet);

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        var own = new StyleDef(1, EcrCode.Create("OWN"));
        typeof(Entity<int>).GetProperty("Id")!.SetValue(own, 77);
        _styles.GetAsync(1, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, StyleDef> { [77] = own });
    }

    private static SaveRowDefCommand Command(int? styleId)
        => new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Intake" },
            null, RowKind.Item, null, false, styleId);

    private SaveRowDefHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user, _styles);

    [Fact]
    public async Task Рядок_зберігає_стиль_своєї_версії_і_повертає_його_в_відповіді()
    {
        var saved = await Save().HandleAsync(1, _table.Id, "7001001", Command(77), CancellationToken.None);

        Assert.Equal(77, saved.StyleId);
        Assert.Equal(77, _table.Rows.Single().StyleId);
    }

    [Fact]
    public async Task Стиль_чужої_версії_відхиляється_422_і_рядок_не_створюється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, _table.Id, "7001001", Command(999), CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0422.presentationValueInvalid", error.Details!["messageKey"]);
        Assert.Equal("StyleId", error.Details["field"]);
        Assert.Empty(_table.Rows);
        await _audit.DidNotReceive().WriteStructureChangeAsync(
            Arg.Any<StructureChangeRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Повторний_PUT_без_стилю_скидає_стиль_рядка()
    {
        await Save().HandleAsync(1, _table.Id, "7001001", Command(77), CancellationToken.None);
        var cleared = await Save().HandleAsync(1, _table.Id, "7001001", Command(null), CancellationToken.None);

        Assert.Null(cleared.StyleId);
        Assert.Null(_table.Rows.Single().StyleId);
    }

    [Fact]
    public async Task Аудит_стану_рядка_несе_стиль()
    {
        await Save().HandleAsync(1, _table.Id, "7001001", Command(77), CancellationToken.None);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "RowDef" && r.NewJson!.Contains("\"StyleId\":77")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Стиль_рядка_і_шапки_є_презентаційними_полями_навіть_з_документами(bool hasDocuments)
    {
        var classifier = new ChangeClassifier();

        Assert.Equal(ChangeClass.Presentation, classifier.Classify("RowDef", nameof(RowDef.StyleId), hasDocuments));
        Assert.Equal(
            ChangeClass.Presentation, classifier.Classify("TableDef", nameof(TableDef.HeaderStyleId), hasDocuments));
        Assert.Contains(nameof(TableDef.HeaderStyleId), ChangeClassifier.PresentationFields);
        Assert.Contains(nameof(RowDef.StyleId), ChangeClassifier.PresentationFields);
    }

    [Fact]
    public void SetStyle_і_SetHeaderStyle_ставлять_і_скидають_посилання()
    {
        var row = new RowDef(_table.Id, Ecr.Domain.ValueObjects.RowKey.Create("R"), 0,
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "R" }), RowKind.Item);

        row.SetStyle(5);
        _table.SetHeaderStyle(6);
        Assert.Equal(5, row.StyleId);
        Assert.Equal(6, _table.HeaderStyleId);

        row.SetStyle(null);
        _table.SetHeaderStyle(null);
        Assert.Null(row.StyleId);
        Assert.Null(_table.HeaderStyleId);
    }
}
