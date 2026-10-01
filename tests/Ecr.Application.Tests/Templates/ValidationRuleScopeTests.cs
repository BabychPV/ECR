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
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ФВ-5.3: область правила — рівно 0..3 (комірка, рядок, таблиця, документ).
/// Раніше <c>Scope=7</c> зберігався й ніколи не виконувався (тихо мертве правило).
/// </summary>
public sealed class ValidationRuleScopeTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TableDef _table;

    public ValidationRuleScopeTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");
        builder.Column(_table, "Volume");

        var draft = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(draft, 1);
        typeof(TemplateVersion)
            .GetField("_sheets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(draft, new List<SheetDef> { sheet });

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private SaveValidationRuleHandler Save()
        => new(_store, new ChangeClassifier(), Substitute.For<IMetadataCache>(), _audit, _uow,
            Substitute.For<IClock>(), _access, _user, new RealFormulaEngine());

    private static SaveValidationRuleCommand Command(byte scope)
        => new(ValidationSeverity.Warning, scope, "[Volume] >= 0",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Bad" }, null, true);

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.3")]
    public async Task Кожна_з_чотирьох_областей_зберігається(byte scope)
    {
        var saved = await Save().HandleAsync(1, _table.Id, "R1", Command(scope), CancellationToken.None);

        Assert.Equal(scope, saved.Scope);
        Assert.Single(_table.ValidationRules);
    }

    [Theory]
    [InlineData((byte)4)]
    [InlineData((byte)7)]
    [InlineData((byte)255)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.3")]
    public async Task Область_поза_0_3_відхиляється_і_нічого_не_зберігається(byte scope)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, _table.Id, "R1", Command(scope), CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.validationScope", error.Details!["messageKey"]);
        Assert.Empty(_table.ValidationRules);
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }
}
