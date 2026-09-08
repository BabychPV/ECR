using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Авторство структури шаблону, продовження W5.0 на <c>ValidationRule</c>
/// (W5.4): та сама форма <c>PUT</c> за кодом, вкладеним у таблицю — за
/// зразком <c>SheetDefTests</c>.
/// </summary>
/// <remarks>
/// ⛔ До цього зрізу <c>ValidationRule</c> створював лише <c>Ecr.DataGen</c> і
/// тести напряму через конструктор: авторства правил валідації через API не
/// існувало взагалі.
/// </remarks>
public sealed class ValidationRuleTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IRepository<ValidationRule, int> _rules = Substitute.For<IRepository<ValidationRule, int>>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly TemplateVersion _draft;
    private readonly TableDef _table;

    public ValidationRuleTests()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");

        _draft = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(_draft, 1);
        typeof(TemplateVersion)
            .GetField("_sheets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_draft, new List<SheetDef> { sheet });

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private static Dictionary<string, string> Message(string en) => new(StringComparer.OrdinalIgnoreCase) { ["en"] = en };

    private static SaveValidationRuleCommand Command(
        string expression = "[Volume] >= 0", ValidationSeverity severity = ValidationSeverity.Error,
        byte scope = 0, int? columnDefId = null, bool isActive = true)
        => new(severity, scope, expression, Message("Порушено"), columnDefId, isActive);

    private SaveValidationRuleHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    private DeleteValidationRuleHandler Delete()
        => new(_store, _rules, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Нове_правило_зберігається_і_потрапляє_в_аудит_структурних_змін()
    {
        var saved = await Save().HandleAsync(1, _table.Id, "R1", Command(), CancellationToken.None);

        Assert.Equal("R1", saved.Code);
        Assert.Equal(_table.Id, saved.TableDefId);
        Assert.Single(_table.ValidationRules);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "ValidationRule" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());

        // ⛔ На відміну від PeriodAccessRuleDef, ValidationRule живе в
        // кешованому знімку (TableDef.ValidationRules із GetWithStructureAsync),
        // тому інвалідація потрібна тут — див. коментар класу SaveValidationRuleHandler.
        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Повторний_запис_тим_самим_кодом_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(1, _table.Id, "R1", Command(expression: "[Volume] >= 0"), CancellationToken.None);
        var updated = await Save().HandleAsync(
            1, _table.Id, "R1", Command(expression: "[Volume] >= 100", severity: ValidationSeverity.Warning),
            CancellationToken.None);

        Assert.Single(_table.ValidationRules);
        Assert.Equal("[Volume] >= 100", updated.Expression);
        Assert.Equal(ValidationSeverity.Warning, updated.Severity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Запис_правила_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, _table.Id, "R1", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_table.ValidationRules);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Запис_у_неіснуючій_таблиці_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save().HandleAsync(1, tableDefId: 999, "R1", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Видалення_прибирає_правило_фізично()
    {
        await Save().HandleAsync(1, _table.Id, "R1", Command(), CancellationToken.None);
        Assert.Single(_table.ValidationRules);
        _metadataCache.ClearReceivedCalls();

        await Delete().HandleAsync(1, _table.Id, "R1", CancellationToken.None);

        // ⛔ На відміну від SheetDef.SoftDelete: ValidationRule видаляється
        // фізично — ніхто не посилається на нього за ідентифікатором.
        //
        // ⚠ `Arg.Any<ValidationRule>()`, а не конкретний екземпляр:
        // `Entity<TId>.Equals` повертає `false` для НЕзбереженої сутності
        // незалежно від посилання (`IsPersisted` тут завжди `false` — мок
        // `SaveChangesAsync` не призначає `Id`), тому звірка на конкретний
        // екземпляр падала б навіть при правильному виклику — той самий
        // прийом, що й `TableRelationTests`.
        _rules.Received(1).Remove(Arg.Any<ValidationRule>());

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_неіснуючого_правила_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(1, _table.Id, "Missing", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_правила_в_опублікованій_версії_відхиляється()
    {
        await Save().HandleAsync(1, _table.Id, "R1", Command(), CancellationToken.None);
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Delete().HandleAsync(1, _table.Id, "R1", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_правило_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, _table.Id, "R1", Command(), CancellationToken.None));

        Assert.Empty(_table.ValidationRules);
    }
}
