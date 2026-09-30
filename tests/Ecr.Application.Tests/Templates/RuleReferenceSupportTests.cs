using Ecr.Application.Errors;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// <c>ФВ-5.9</c>: що правило валідації може читати. Комірки СВОЄЇ таблиці — так;
/// константи, інша таблиця, аркуш чи період — ні, і відмова ЯВНА (на збереженні й на публікації).
/// </summary>
/// <remarks>
/// ⛔ Звірка вимог 2026-09-30 (№11): <c>GetConstant → #REF</c>, а міжтабличні посилання
/// <c>ValidationEngine</c> читав мовчки НЕ ТИМ (<c>ScopeContext.Read</c> ігнорує таблицю й
/// зсув періоду) — публікація такого не відхиляла. Реалізувати читання констант/інших
/// таблиць у синхронному контексті правил не робилося (лишається обмеженням); замість
/// тихої неправди — відмова з ключем.
/// </remarks>
public sealed class RuleReferenceSupportTests
{
    private static readonly RealFormulaEngine Engine = new();

    [Theory]
    [InlineData("[Jan] > 0")]
    [InlineData("[R1].[Jan] > 0")]
    [InlineData("IF([Jan] > 0, [Jan] < 100, TRUE)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.9")]
    public void Комірки_своєї_таблиці_приймаються(string expression)
        => RuleExpressionChecks.RequireSupportedReferences(Engine, expression);

    [Theory]
    [InlineData("[Water].[R1].[Jan] > 0", "[Water].[Jan]")]
    [InlineData("[Sheet1].[Water].[R1].[Jan] > 0", "[Sheet1].[Water].[Jan]")]
    [InlineData("[Period:-1].[Jan] > 0", "[Period:-1].[Jan]")]
    [InlineData("[Jan] > 0 AND [Period:-1].[Jan] > 0", "[Period:-1].[Jan]")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.9")]
    public void Інша_таблиця_аркуш_чи_період_відхиляються_при_збереженні_з_ключем(string expression, string reference)
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => RuleExpressionChecks.RequireSupportedReferences(Engine, expression));

        Assert.Equal(RuleExpressionChecks.UnsupportedReferenceKey, error.Details!["messageKey"]);
        Assert.Equal(reference, error.Details["reference"]);
    }

    /// <summary>Версія з таблицею <c>Main</c> (колонка <c>Jan</c>) і одним правилом.</summary>
    private static TemplateVersion VersionWithRule(string expression)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan");

        table.AddValidationRule(new ValidationRule(
            table.Id, EcrCode.Create("R1"), ValidationSeverity.Error, scope: 1, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "rule" })));

        var version = new TemplateVersion(1, "1.0.0.0", 7, DateTime.UnixEpoch);
        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(version, 1);
        version.GetType()
            .GetField("_sheets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(version, new List<SheetDef> { sheet });

        return version;
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати виклик <c>CheckSupportedReferences</c> з
    /// <c>RuleExpressionChecks.Check</c> — тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.9")]
    public void Публікація_відхиляє_правило_з_посиланням_на_іншу_таблицю()
    {
        var diagnostics = RuleExpressionChecks.Check(VersionWithRule("[Other].[R1].[Jan] > 0"), Engine);

        Assert.Contains(diagnostics, d => d.MessageKey == RuleExpressionChecks.UnsupportedReferenceKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.9")]
    public void Публікація_відхиляє_правило_з_константою_методології()
    {
        // Константи в правилах не підтримані за побудовою (контекст правила бачить лише дані
        // документа): діалект шаблону відхиляє `CST.X` ще парсером — тест фіксує це для правил.
        var diagnostics = RuleExpressionChecks.Check(VersionWithRule("[Jan] < CST.LIMIT"), Engine);

        Assert.Contains(diagnostics, d => d.MessageKey == "expr.methodologyConstructInTemplate");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.9")]
    public void Публікація_приймає_правило_над_комірками_своєї_таблиці()
    {
        Assert.Empty(RuleExpressionChecks.Check(VersionWithRule("[Jan] >= 0"), Engine));
    }
}
