// tests/Ecr.Application.Tests/Documents/GetValidationResultHandlerTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// B-11 follow-up: <c>GetValidationResultHandler</c> має перерезолвити
/// <c>Message</c> мовою ПОТОЧНОГО читача, а не довіряти тексту, збереженому
/// мовою того, хто востаннє запустив перевірку.
/// </summary>
public sealed class GetValidationResultHandlerTests
{
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int TemplateVersionId = 2;
    private const int TableDefId = 3;

    private readonly IValidationResultStore _results = Substitute.For<IValidationResultStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public GetValidationResultHandlerTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.View").Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _documents.GetTemplateVersionIdAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionId);
    }

    private GetValidationResultHandler Handler() => new(_results, _documents, _metadata, _access, _user);

    private static SheetDef SheetWithRule(string ruleCode, LocalizedText message)
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(TableDefId, EcrCode.Create("T3"), Text("T3"), 1,
                                  TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDefId);
        sheet.AddTable(table);

        table.AddValidationRule(new ValidationRule(
            TableDefId, EcrCode.Create(ruleCode), ValidationSeverity.Error, scope: 1, "true", message));

        return sheet;
    }

    private void SetSnapshot(SheetDef sheet)
    {
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(TemplateVersionId, 0, [sheet],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int, string), RowDef>()));
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Domain.Abstractions.Entity<int>
        => typeof(Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(e, id);

    private static ValidationMessage StoredMessage(string ruleCode, string message)
        => new(ValidationSeverity.Error, ruleCode, message, TableDefId, "row-1", "col-1", BlocksSave: true);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "B-11")]
    public async Task Підсумок_повертається_мовою_читача_а_не_автора_перевірки()
    {
        // Збережений текст — мовою АВТОРА запуску (ru), як його поклав
        // ValidateDocumentHandler.
        var stored = new List<ValidationMessage> { StoredMessage("REQ", "Обязательное поле") };
        _results.GetLatestAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new ValidationSummary(DocumentId, Period, DateTime.UtcNow, 1, 0, 0, JsonSerializer.Serialize(stored)));

        // Живий знімок версії несе ОБИДВА переклади правила.
        var ruleMessage = new LocalizedText(new Dictionary<string, string>
        {
            ["en"] = "Field is required",
            ["ru"] = "Обязательное поле",
        });
        SetSnapshot(SheetWithRule("REQ", ruleMessage));

        // Читач — англомовний.
        _user.Language.Returns("en");

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.NotNull(result);
        var message = Assert.Single(result!);
        // Мутаційний доказ: якби перерезолву не було, тут лишився б
        // збережений "Обязательное поле" — саме той рядок, що впав би, якби
        // прибрати виклик rule.MessageL10n.Get(language) з фікса.
        Assert.Equal("Field is required", message.Message);
        Assert.NotEqual("Обязательное поле", message.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "B-11")]
    public async Task Правило_видалене_з_часу_перевірки_лишає_збережений_текст()
    {
        // Код правила в збережених повідомленнях ("OLD") відсутній у
        // поточному знімку версії (правило перейменували чи видалили).
        var stored = new List<ValidationMessage> { StoredMessage("OLD", "Застаріле повідомлення") };
        _results.GetLatestAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new ValidationSummary(DocumentId, Period, DateTime.UtcNow, 1, 0, 0, JsonSerializer.Serialize(stored)));

        // Знімок несе інше правило ("REQ"), не те, що в збережених даних.
        SetSnapshot(SheetWithRule("REQ", Text("Field is required")));

        _user.Language.Returns("en");

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.NotNull(result);
        var message = Assert.Single(result!);
        // Запасний варіант: збережений текст, БЕЗ винятку.
        Assert.Equal("Застаріле повідомлення", message.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Порожній_перелік_не_ходить_у_метадані()
    {
        _results.GetLatestAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new ValidationSummary(DocumentId, Period, DateTime.UtcNow, 0, 0, 0, JsonSerializer.Serialize(new List<ValidationMessage>())));

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!);

        // Дешевий шлях: жодного походу в IDocumentStore/IMetadataCache.
        await _documents.DidNotReceive().GetTemplateVersionIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _metadata.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Перевірку_не_запускали_повертає_null()
    {
        _results.GetLatestAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns((ValidationSummary?)null);

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.Null(result);
    }
}
