using Ecr.Application.Ports;
using Ecr.Application.Tests.Recalculation;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Validation;

/// <summary>
/// P-5 (аудит 2026-11-10): <see cref="ValidationEngine"/> розбирає вираз правила ОДИН раз на екземпляр (scoped = запит),
/// а не на кожен рядок «Перевірити»/подання і кожну комірку PATCH — як перерахунок (<c>CountingFormulaEngine.ParseCalls</c>).
/// </summary>
/// <remarks>
/// Мутації: повернути в <c>Evaluate</c> прямий <c>formulaEngine.Parse</c> — тести (а) і (б) червоні (501 / 300 розборів);
/// не кешувати розбір із помилкою — тест (в) червоний за лічильником.
/// </remarks>
public sealed class ValidationRuleParseOnceTests
{
    private static readonly PeriodKey Period = new(202601);
    private static readonly IReadOnlyDictionary<string, ExpressionValue> NoHeaders = new Dictionary<string, ExpressionValue>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Правило_рядка_й_таблиці_розбираються_по_разу_на_прогін_таблиці()
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("S"), "T", TableRowMode.Dynamic);
        var c0 = builder.Column(table, "C0");
        table.AddValidationRule(Rule(table, "ROW", scope: 1, "[C0] >= 0"));
        // Інший текст: ключ кешу — сам вираз, однакові тексти розбиралися б один раз.
        table.AddValidationRule(Rule(table, "TABLE", scope: 2, "[C0] > -1"));
        var snapshot = builder.Build();

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var cells = new List<CellRecord>();
        for (var r = 0; r < 500; r++)
        {
            rowIds[$"R{r}"] = r + 1;
            cells.Add(new CellRecord(new CellAddress(Period, r + 1, c0.Id), table.Id, new CellValueData { ValueNumeric = 1m }));
        }

        var counting = new CountingFormulaEngine();

        var messages = await TableValidation.RunAsync(
            new ValidationEngine(counting), Substitute.For<IRegistryStore>(), snapshot, table, cells, rowIds, NoHeaders, "en", default);

        Assert.Empty(messages);
        Assert.Equal(2, counting.ParseCalls);
        Assert.Equal(501, counting.EvaluateCalls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Коміркове_правило_розбирається_раз_на_300_комірок_одного_запиту()
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("S"), "T", TableRowMode.Dynamic);
        var column = builder.Column(table, "Mass");
        IReadOnlyList<ValidationRule> rules = [Rule(table, "NONNEG", scope: 0, "[Mass] >= 0")];

        var counting = new CountingFormulaEngine();
        var engine = new ValidationEngine(counting);

        for (var i = 0; i < 300; i++)
        {
            var messages = engine.ValidateCell(column, new CellValueData { ValueNumeric = i % 2 == 0 ? 1m : -1m }, rules, NoHeaders, "en");
            Assert.Equal(i % 2 == 0 ? 0 : 1, messages.Count(m => m.RuleCode == "NONNEG"));
        }

        Assert.Equal(1, counting.ParseCalls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Зламаний_вираз_дає_ECR_VAL_RULE_на_кожному_застосуванні_а_розбирається_раз()
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("S"), "T", TableRowMode.Dynamic);
        var column = builder.Column(table, "Mass");
        IReadOnlyList<ValidationRule> rules = [Rule(table, "BROKEN", scope: 0, "[Mass] >= (")];

        var counting = new CountingFormulaEngine();
        var engine = new ValidationEngine(counting);

        for (var i = 0; i < 3; i++)
        {
            var message = Assert.Single(engine.ValidateCell(column, new CellValueData { ValueNumeric = 1m }, rules, NoHeaders, "en"));
            Assert.Equal(ValidationEngine.BrokenRuleCode, message.RuleCode);
            Assert.Equal(ValidationMessageTemplates.RuleParseError, message.MessageKey);
        }

        Assert.Equal(1, counting.ParseCalls);
    }

    private static ValidationRule Rule(TableDef table, string code, byte scope, string expression)
        => new(
            table.Id, EcrCode.Create(code), ValidationSeverity.Error, scope, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
}
