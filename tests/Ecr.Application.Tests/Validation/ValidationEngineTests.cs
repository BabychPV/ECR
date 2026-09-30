// tests/Ecr.Application.Tests/Validation/ValidationEngineTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Validation;

/// <summary>
/// Валідація. **Блокує збереження лише комірковий `Error`** (D-90): заборона
/// зберегти проміжний стан зробила б роботу з великою таблицею неможливою.
/// </summary>
public sealed class ValidationEngineTests
{
    /// <summary>Справжній рушій, а не заглушка: перевіряються самі правила.</summary>
    private static ValidationEngine Engine() => new(new RealFormulaEngine());

    /// <summary>Порожня шапка документа — ці тести не про <c>HDR.X</c>.</summary>
    private static readonly IReadOnlyDictionary<string, ExpressionValue> NoHeaders =
        new Dictionary<string, ExpressionValue>();

    private static ColumnDef Column(string code, CellDataType type = CellDataType.Decimal)
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }), 1, type);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(column, 11);
        return column;
    }

    private static ValidationRule Rule(
        string code, ValidationSeverity severity, byte scope, string expression)
        => new(tableDefId: 3, EcrCode.Create(code), severity, scope, expression,
               new LocalizedText(new Dictionary<string, string> { ["en"] = $"Порушено {code}" }));

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.8")]
    public void Комірковий_Error_блокує_запис()
    {
        var messages = Engine().ValidateCell(
            Column("Volume"),
            new CellValueData { ValueNumeric = -5m },
            [Rule("POSITIVE", ValidationSeverity.Error, scope: 0, "[Volume] >= 0")],
            NoHeaders, "en");

        var message = Assert.Single(messages);
        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.True(message.BlocksSave);
        Assert.Equal("POSITIVE", message.RuleCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.1")]
    [Trait("Requirement", "ФВ-5.18")]
    public void Error_рівня_документа_блокує_Submit_але_не_запис()
    {
        var messages = Engine().ValidateScope(
            scope: 3,
            [Rule("BALANCE", ValidationSeverity.Error, scope: 3, "[Total] = 0")],
            new Values { ["Total"] = 42m },
            NoHeaders, "en");

        var message = Assert.Single(messages);

        // ⚠ Рівень той самий — Error, а наслідок інший. Різниця не в
        // суворості тексту, а в тому, ЩО правило блокує (R-B3): заборона
        // зберегти проміжний стан зробила б заповнення великої таблиці
        // неможливим, бо баланс сходиться лише наприкінці.
        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.False(message.BlocksSave);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.3")]
    [Trait("Requirement", "ФВ-5.2")]
    public void Зламане_правило_дає_Warning_про_правило_а_не_Error_даних()
    {
        var messages = Engine().ValidateCell(
            Column("Volume"),
            new CellValueData { ValueNumeric = 10m },
            [Rule("BROKEN", ValidationSeverity.Error, scope: 0, "[Volume] >>> 0")],
            NoHeaders, "en");

        var message = Assert.Single(messages);

        // Інакше зламане правило заблокувало б роботу з цілком коректними
        // даними, а виправити його змогла б лише людина з доступом до
        // конфігурації — тобто не та, що зараз заповнює звіт.
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.False(message.BlocksSave);
        Assert.Equal(ValidationEngine.BrokenRuleCode, message.RuleCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.5")]
    public void Результат_не_залежить_від_порядку_правил()
    {
        var a = Rule("A", ValidationSeverity.Error, scope: 0, "[Volume] >= 0");
        var b = Rule("B", ValidationSeverity.Warning, scope: 0, "[Volume] <= 100");
        var c = Rule("C", ValidationSeverity.Info, scope: 0, "[Volume] <> 0");
        var value = new CellValueData { ValueNumeric = -5m };

        var forward = Engine().ValidateCell(Column("Volume"), value, [a, b, c], NoHeaders, "en");
        var backward = Engine().ValidateCell(Column("Volume"), value, [c, b, a], NoHeaders, "en");

        // Правила не мають між собою порядку виконання: їхній набір — це
        // множина, а не програма. Залежність від порядку означала б, що
        // додавання правила тихо змінює результат наявних.
        Assert.Equal(
            forward.Select(m => m.RuleCode).OrderBy(x => x, StringComparer.Ordinal),
            backward.Select(m => m.RuleCode).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.8")]
    public void Порожня_обовязкова_комірка_дає_людський_текст_а_не_код()
    {
        var column = Column("Volume");
        column.SetRequired(true);

        var messages = Engine().ValidateCell(column, CellValueData.Empty, [], NoHeaders, "en");

        var message = Assert.Single(messages);

        // ⛔ До фіксу тут стояло буквально "ECR-CELL-0422" — той самий рядок,
        // що йде в `RuleCode`, копіювався і в `Message`, і саме цей текст
        // бачив оператор у тултипі комірки: `Message` передається клієнту
        // через `PatchCellsHandler.EnsureValidationPasses` як `cells[].message`.
        Assert.Equal("ECR-CELL-0422", message.RuleCode);
        Assert.NotEqual("ECR-CELL-0422", message.Message);
        Assert.Contains(column.Code, message.Message, StringComparison.Ordinal);
        Assert.True(message.BlocksSave);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.6")]
    public void Результат_валідації_переживає_перезавантаження()
    {
        var column = Column("Volume");
        var rules = new[] { Rule("POSITIVE", ValidationSeverity.Error, scope: 0, "[Volume] >= 0") };
        var value = new CellValueData { ValueNumeric = -5m };

        var before = Engine().ValidateCell(column, value, rules, NoHeaders, "en");

        // «Переживає перезавантаження» означає, що результат — ФУНКЦІЯ від
        // збережених даних і конфігурації, а не від стану процесу. Новий
        // рушій, новий набір об'єктів, ті самі вхідні — та сама відповідь.
        var after = Engine().ValidateCell(column, value, rules, NoHeaders, "en");

        Assert.Equal(
            before.Select(m => (m.Severity, m.RuleCode, m.BlocksSave)),
            after.Select(m => (m.Severity, m.RuleCode, m.BlocksSave)));
        Assert.NotEmpty(before);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B-11")]
    public void Текст_правила_іде_мовою_запиту_а_не_завжди_англійською()
    {
        // ⛔ B-11 (UX-аудит, четвертий раунд): було жорстко `.Get("en")` —
        // переклад правила, який методолог заклав для RU, ігнорувався.
        var rule = new ValidationRule(
            tableDefId: 3, EcrCode.Create("RU_RULE"), ValidationSeverity.Warning, scope: 0, "[Volume] >= 0",
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Must be positive", ["ru"] = "Должно быть положительным" }));

        var messages = Engine().ValidateCell(
            Column("Volume"), new CellValueData { ValueNumeric = -5m }, [rule], NoHeaders, "ru");

        var message = Assert.Single(messages);
        Assert.Equal("Должно быть положительным", message.Message);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B-11")]
    public void Текст_правила_без_перекладу_на_мову_запиту_підміняється_англійським()
    {
        // LocalizedText.Get сам робить fallback на "en" — рушій не має його дублювати.
        var messages = Engine().ValidateCell(
            Column("Volume"), new CellValueData { ValueNumeric = -5m },
            [Rule("POSITIVE", ValidationSeverity.Warning, scope: 0, "[Volume] >= 0")], NoHeaders, "kz");

        var message = Assert.Single(messages);
        Assert.Equal("Порушено POSITIVE", message.Message);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B-11")]
    public void Обовязкова_комірка_рос_мовою_дає_російський_текст()
    {
        var column = Column("Volume");
        column.SetRequired(true);

        var messages = Engine().ValidateCell(column, CellValueData.Empty, [], NoHeaders, "ru");

        var message = Assert.Single(messages);
        Assert.Contains("обязательна", message.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("обов'язкова", message.Message, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B-11")]
    public void Порожня_мова_запиту_не_валить_валідацію_а_підміняється_англійською()
    {
        // ⚠ Виклик із порожньої/незаданої ICurrentUser.Language (типово в
        // тестових підробках) не має права заблокувати запис комірки —
        // м'який fallback, а не ArgumentException.
        var messages = Engine().ValidateCell(
            Column("Volume"), new CellValueData { ValueNumeric = -5m },
            [Rule("POSITIVE", ValidationSeverity.Warning, scope: 0, "[Volume] >= 0")], NoHeaders, "   ");

        var message = Assert.Single(messages);
        Assert.Equal("Порушено POSITIVE", message.Message);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B-11")]
    public void Зламане_правило_дає_текст_мовою_запиту()
    {
        var messages = Engine().ValidateCell(
            Column("Volume"), new CellValueData { ValueNumeric = 10m },
            [Rule("BROKEN", ValidationSeverity.Error, scope: 0, "[Volume] >>> 0")], NoHeaders, "ru");

        var message = Assert.Single(messages);
        Assert.Equal(ValidationEngine.BrokenRuleCode, message.RuleCode);

        // ⛔ До фіксу тут завжди йшло українське речення незалежно від мови.
        Assert.DoesNotContain("розбирається", message.Message, StringComparison.Ordinal);
        Assert.Contains("разбирается", message.Message, StringComparison.Ordinal);
    }

    /// <summary>Значення рядка для правил рівня рядка й вище.</summary>
    private sealed class Values : Dictionary<string, object?>, IValidationContext
    {
        public object? GetCell(string columnCode) => this.GetValueOrDefault(columnCode);

        public object? GetCell(string rowKey, string columnCode) => GetCell(columnCode);
    }
}
