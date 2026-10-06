using System.Globalization;
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
/// Довжина виразу правила валідації і формули перевіряється за довжиною КОЛОНКИ (A4-03).
/// </summary>
/// <remarks>
/// ⛔ Приймальна №4: правило `[Mass] &lt;= 1+1+…` на 1000–1024 ланки (2000–4000 символів) проходило
/// межу 4000 валідатора, але не колонку `cfg.ValidationRule.Expression` (2000) — користувач бачив
/// 500 `ECR-SYS-0500` (SQL truncated). Тепер відмова — 422 `expressionTooLong` ДО звернення до БД.
/// </remarks>
public sealed class ExpressionColumnLengthTests
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
    private readonly TableDef _table;
    private readonly ColumnDef _total;

    public ExpressionColumnLengthTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");
        builder.Column(_table, "Mass");
        _total = builder.Column(_table, "Total", CellDataType.Formula);

        var draft = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(draft, 1);
        typeof(TemplateVersion)
            .GetField("_sheets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(draft, new List<SheetDef> { sheet });

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    /// <summary>`[Mass] &lt;= 1+1+…` рівно заданої довжини в символах.</summary>
    private static string ChainOfLength(int length)
    {
        const string head = "[Mass] <= 1";
        var tail = string.Concat(Enumerable.Repeat("+1", (length - head.Length) / 2));
        var text = head + tail;
        return text.Length == length ? text : text + new string('1', length - text.Length);
    }

    private static Dictionary<string, string> Message() => new(StringComparer.OrdinalIgnoreCase) { ["en"] = "m" };

    private SaveValidationRuleHandler SaveRule()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user,
            new RealFormulaEngine());

    private SaveFormulaDefHandler SaveFormula()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user,
            new RealFormulaEngine());

    [Theory]
    [InlineData(2001)]
    [InlineData(2050)]
    [InlineData(4000)]
    public async Task Правило_валідації_довше_за_колонку_422_людським_ключем_і_нічого_не_пишеться(int length)
    {
        var text = ChainOfLength(length);
        Assert.Equal(length, text.Length);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SaveRule().HandleAsync(
            1, _table.Id, "R1", new SaveValidationRuleCommand(ValidationSeverity.Error, 0, text, Message(), null, true),
            CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", error.Details!["messageKey"]);
        Assert.Equal("2000", error.Details["max"]);
        Assert.Equal(length.ToString(CultureInfo.InvariantCulture), error.Details["length"]);
        Assert.Empty(_table.ValidationRules);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Правило_валідації_рівно_2000_символів_доходить_до_збереження()
    {
        var text = ChainOfLength(2000);

        await SaveRule().HandleAsync(
            1, _table.Id, "R1", new SaveValidationRuleCommand(ValidationSeverity.Error, 0, text, Message(), null, true),
            CancellationToken.None);

        Assert.Single(_table.ValidationRules);
    }

    [Theory]
    [InlineData(2001)]
    [InlineData(4000)]
    public async Task Формула_колонки_довша_за_колонку_422_і_нічого_не_пишеться(int length)
    {
        var text = "[Mass]" + string.Concat(Enumerable.Repeat("+1", (length - 6) / 2));
        text += new string('1', length - text.Length);
        Assert.Equal(length, text.Length);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SaveFormula().HandleAsync(
            1, _table.Id, FormulaScope.Column, _total.Id.ToString(CultureInfo.InvariantCulture),
            new SaveFormulaDefCommand(ExpressionDialect.Template, text),
            CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", error.Details!["messageKey"]);
        Assert.Equal("2000", error.Details["max"]);
        Assert.Equal(length.ToString(CultureInfo.InvariantCulture), error.Details["length"]);
        Assert.Empty(_table.Formulas);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Межі_колонок_єдине_джерело_у_домені()
    {
        Assert.Equal(2000, ValidationRule.MaxExpressionLength);
        Assert.Equal(2000, FormulaDef.MaxExpressionLength);
        Assert.Equal(4000, RegistryRuleDef.MaxExpressionLength);
    }
}