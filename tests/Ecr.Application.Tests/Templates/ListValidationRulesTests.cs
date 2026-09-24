using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Перелік правил валідації таблиці (X-15, четвертий раунд UX): діалог
/// видаляв правило введеним з пам'яті кодом, бо жоден маршрут їх не віддавав.
/// </summary>
public sealed class ListValidationRulesTests
{
    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly TableDef _table;
    private readonly TableDef _other;

    public ListValidationRulesTests()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");
        _other = builder.Table(sheet, "Other");

        var version = new TemplateVersion(1, "1.0.0.0", 7, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        version.AddSheet(sheet);

        _table.AddValidationRule(Rule(_table.Id, "Zeta"));
        _table.AddValidationRule(Rule(_table.Id, "Alpha"));
        _other.AddValidationRule(Rule(_other.Id, "Foreign"));

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());
        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(version);
    }

    private ListValidationRulesHandler Handler() => new(_store, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Перелік_віддає_правила_лише_цієї_таблиці_в_порядку_коду()
    {
        var rules = await Handler().HandleAsync(1, _table.Id, CancellationToken.None);

        Assert.Equal(["Alpha", "Zeta"], rules.Select(r => r.Code));
        Assert.All(rules, r => Assert.Equal(_table.Id, r.TableDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Невідома_таблиця_404()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(1, 999, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_перегляду_перелік_не_віддається()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(1, _table.Id, CancellationToken.None));
    }

    private static ValidationRule Rule(int tableDefId, string code)
        => new(
            tableDefId, EcrCode.Create(code), ValidationSeverity.Error, 0, "true",
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
}
