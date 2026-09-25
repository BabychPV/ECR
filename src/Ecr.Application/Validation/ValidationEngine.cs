using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Validation;

/// <summary>
/// Виконує правила валідації. Рівні розрізняються не за суворістю тексту, а
/// за тим, що вони блокують (R-B3): комірковий <c>Error</c> блокує запис,
/// решта — лише подання.
/// </summary>
public sealed class ValidationEngine(IFormulaEngine formulaEngine)
{
    /// <summary>Код, під яким повідомляється про несправне правило.</summary>
    /// <remarks>
    /// ⚠ Не збігається з кодом жодного правила навмисно: це повідомлення про
    /// КОНФІГУРАЦІЮ, а не про дані, і плутати їх не можна.
    /// </remarks>
    public const string BrokenRuleCode = "ECR-VAL-RULE";

    /// <summary>Перевіряє одну комірку — виконується синхронно на шляху запису.</summary>
    /// <param name="column">Колонка, до якої належить значення.</param>
    /// <param name="value">Значення комірки.</param>
    /// <param name="rules">Правила таблиці; беруться лише з <c>Scope = 0</c>.</param>
    /// <param name="headers">
    /// Значення шапки документа, ключовані кодом поля — для <c>HDR.X</c> у
    /// виразі правила; порожній словник — прогін без шапки.
    /// </param>
    /// <param name="language">
    /// Мова запиту (<c>en</c>/<c>ru</c>/<c>kz</c>) — <c>ValidationMessage.Message</c>
    /// їде клієнту НАПРЯМУ, без проходу крізь резолвер <c>messageKey</c>
    /// (B-11, UX-аудит, четвертий раунд): ці повідомлення несуть готовий
    /// текст УЖЕ, і клієнт показує його як є.
    /// </param>
    public IReadOnlyList<ValidationMessage> ValidateCell(
        ColumnDef column, Domain.ValueObjects.CellValueData value, IReadOnlyList<ValidationRule> rules,
        IReadOnlyDictionary<string, ExpressionValue> headers, string language)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(headers);

        // ⚠ М'який запасний варіант, а не ArgumentException: на відміну від
        // решти конвеєра (де мову несе HTTP-запит і вона гарантовано непорожня,
        // ICurrentUser.CurrentUser.Language), тут викликач — код запису
        // комірки, і порожня мова НЕ має права заблокувати збереження даних.
        // Той самий fallback, що вже дає LocalizedText.Get(language, "en").
        language = string.IsNullOrWhiteSpace(language) ? "en" : language;

        var messages = new List<ValidationMessage>();

        // Спершу СТРУКТУРНА перевірка: тип, обов'язковість, довідник, одиниця.
        // Вона не залежить від конфігурації правил і тому не може «зламатися».
        if (column.ValidateValue(value) is { } structural)
        {
            messages.Add(new ValidationMessage(
                ValidationSeverity.Error, "ECR-CELL-0422", StructuralMessage(column, value, structural, language),
                column.TableDefId, null, column.Code, BlocksSave: true));
        }

        foreach (var rule in rules.Where(r => r.IsActive && r.Scope == 0))
        {
            if (rule.ColumnDefId is { } columnId && columnId != column.Id)
            {
                continue;
            }

            Evaluate(rule, CellContext(column, value, headers), column.TableDefId, null, column.Code, language, messages);
        }

        // ⚠ Повертаються ВСІ порушення, а не перше: користувач має побачити
        // список того, що виправити, а не отримувати їх по одному.
        return messages;
    }

    /// <summary>Перевіряє рядок, таблицю або документ — не блокує запис.</summary>
    /// <param name="scope">Рівень правил: 1 рядок, 2 таблиця, 3 документ.</param>
    /// <param name="rules">Правила таблиці.</param>
    /// <param name="context">Джерело значень для виразів правил.</param>
    /// <param name="headers">
    /// Значення шапки документа, ключовані кодом поля — для <c>HDR.X</c> у
    /// виразі правила; порожній словник — прогін без шапки.
    /// </param>
    /// <param name="language">Мова запиту (B-11) — див. <see cref="ValidateCell"/>.</param>
    public IReadOnlyList<ValidationMessage> ValidateScope(
        byte scope, IReadOnlyList<ValidationRule> rules, IValidationContext context,
        IReadOnlyDictionary<string, ExpressionValue> headers, string language)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(headers);

        language = string.IsNullOrWhiteSpace(language) ? "en" : language;

        var messages = new List<ValidationMessage>();

        foreach (var rule in rules.Where(r => r.IsActive && r.Scope == scope))
        {
            Evaluate(rule, new ScopeContext(context) { Headers = headers }, rule.TableDefId, null, null, language, messages);
        }

        return messages;
    }

    private void Evaluate(
        ValidationRule rule,
        IEvaluationContext context,
        int tableDefId,
        string? rowKey,
        string? columnCode,
        string language,
        List<ValidationMessage> messages)
    {
        var parsed = formulaEngine.Parse(rule.Expression, ExpressionDialect.Template);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            messages.Add(Broken(rule, tableDefId, rowKey, columnCode,
                L(language,
                    en: $"Rule '{rule.Code}' does not parse: {(parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : string.Empty)}",
                    ru: $"Правило '{rule.Code}' не разбирается: {(parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : string.Empty)}",
                    kz: $"'{rule.Code}' ережесі талдана алмайды: {(parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : string.Empty)}")));
            return;
        }

        var result = formulaEngine.Evaluate(parsed.Expression, context);
        var value = result.Value;

        // ⚠ Помилка ОБЧИСЛЕННЯ виразу — це Warning про несправне ПРАВИЛО, а не
        // Error даних. Інакше зламане правило заблокувало б роботу з цілком
        // коректними даними, і виправити його змогла б лише людина з доступом
        // до конфігурації — тобто не той, хто зараз заповнює звіт.
        if (value.IsError || value.Type != ExpressionValueType.Boolean)
        {
            var reason = value.ErrorCode ?? value.Type.ToString();
            messages.Add(Broken(rule, tableDefId, rowKey, columnCode,
                L(language,
                    en: $"Rule '{rule.Code}' did not return a logical answer: {reason}",
                    ru: $"Правило '{rule.Code}' не дало логического ответа: {reason}",
                    kz: $"'{rule.Code}' ережесі логикалық жауап бермеді: {reason}")));
            return;
        }

        if ((bool)value.Value!)
        {
            return;
        }

        // ⛔ B-11 (UX-аудит, четвертий раунд): було жорстко `.Get("en")`
        // незалежно від того, якою мовою прийшов запит — RU/KZ-користувач
        // бачив АНГЛІЙСЬКИЙ текст правила, хоча методолог, який писав правило,
        // переклад дав. `LocalizedText.Get` сам робить fallback на `en`, коли
        // перекладу для мови запиту немає.
        messages.Add(new ValidationMessage(
            rule.Severity, rule.Code, rule.MessageL10n.Get(language) ?? rule.Code, tableDefId, rowKey, columnCode,
            // Блокує запис ЛИШЕ комірковий Error (R-B3, D-90): заборона
            // зберегти проміжний стан зробила б роботу з великою таблицею
            // неможливою.
            rule.BlocksSave));
    }

    private static ValidationMessage Broken(
        ValidationRule rule, int tableDefId, string? rowKey, string? columnCode, string message)
        => new(ValidationSeverity.Warning, BrokenRuleCode, message, tableDefId, rowKey, columnCode, BlocksSave: false);

    /// <summary>
    /// Текст структурного порушення для клієнта.
    /// </summary>
    /// <remarks>
    /// ⛔ До цього виправлення тут ішов ГОЛИЙ код <paramref name="structuralCode"/>
    /// (<c>"ECR-CELL-0422"</c>) — те саме значення, що <c>ColumnDef.ValidateValue</c>
    /// повертає як код, а не як текст. Найчастіший шлях сюди — очищена
    /// обов'язкова комірка: оператор бачив у підказці буквально код помилки
    /// замість пояснення, що робити.
    ///
    /// ⚠ Розпізнається лише ЦЕЙ конкретний випадок (порожнє значення в
    /// обов'язковій колонці — той самий предикат, що й гілка 3 у
    /// <c>ColumnDef.ValidateValue</c>). Решта причин <c>ECR-CELL-0422</c>
    /// (невідповідність типу, точність/масштаб) на практиці не долітають
    /// сюди непоміченими: тип відсіює <c>CellValueReader</c> зі своїм
    /// людським текстом ДО виклику цього методу — див. коментар класу.
    /// Розширювати цей метод на решту причин — окрема задача, не ця.
    /// </remarks>
    private static string StructuralMessage(
        ColumnDef column, Domain.ValueObjects.CellValueData value, string structuralCode, string language)
        => structuralCode == "ECR-CELL-0422" && column.IsRequired && value.IsEmpty && value.IsWellFormed()
            ? L(language,
                en: $"Column \"{column.Code}\" is required.",
                ru: $"Колонка «{column.Code}» обязательна.",
                kz: $"«{column.Code}» бағаны міндетті.")
            : structuralCode;

    /// <summary>
    /// Три готові речення двигуна (не з <c>ValidationRule.MessageL10n</c> —
    /// їх ніхто не авторить, вони описують сам механізм валідації), обрані за
    /// мовою запиту (B-11).
    /// </summary>
    /// <remarks>
    /// ⚠ Не через каталог <c>sys_ecr.UiString</c>/<c>messageKey</c>:
    /// <c>ValidationMessage.Message</c> — не деталь виключення, а поле
    /// НОРМАЛЬНОЇ (200) відповіді, яку клієнт показує як є (<c>ValidateCell</c>/
    /// <c>ValidateScope</c> синхронні, каталог читається лише асинхронно). Три
    /// мови продукту — фіксований, закритий список (D-95), тож `switch`
    /// без резолвера — не борг, а форма, симетрична самому переліку мов.
    /// </remarks>
    private static string L(string language, string en, string ru, string kz) => language switch
    {
        "ru" => ru,
        "kz" => kz,
        _ => en,
    };

    private static SingleCellContext CellContext(
        ColumnDef column, Domain.ValueObjects.CellValueData value, IReadOnlyDictionary<string, ExpressionValue> headers)
        => new SingleCellContext(column.Code, CellValueMapping.ToExpressionValue(value, ExpressionValue.Null))
        {
            Headers = headers,
        };

    /// <summary>Контекст правила рівня комірки: видно рівно одну колонку.</summary>
    private sealed class SingleCellContext(string columnCode, ExpressionValue value) : ValidationEvaluationContext
    {
        public override IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
        {
            ArgumentNullException.ThrowIfNull(reference);
            return [string.Equals(reference.ColumnSelector, columnCode, StringComparison.OrdinalIgnoreCase)
                ? value
                : ExpressionValue.Null];
        }
    }

    /// <summary>Контекст правила рівня рядка і вище.</summary>
    private sealed class ScopeContext(IValidationContext inner) : ValidationEvaluationContext
    {
        public override IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
        {
            ArgumentNullException.ThrowIfNull(reference);

            var raw = reference.Row is RowSelector.Single single
                ? inner.GetCell(single.RowKey, reference.ColumnSelector)
                : inner.GetCell(reference.ColumnSelector);

            return [FromObject(raw)];
        }

        private static ExpressionValue FromObject(object? raw)
            => raw switch
            {
                null => ExpressionValue.Null,
                decimal number => ExpressionValue.Number(number),
                int number => ExpressionValue.Number(number),
                bool flag => ExpressionValue.Boolean(flag),
                DateTime date => ExpressionValue.Date(date),
                _ => ExpressionValue.Text(raw.ToString() ?? string.Empty),
            };
    }
}

/// <summary>Контекст для правил рівня рядка і вище.</summary>
public interface IValidationContext
{
    /// <summary>Значення комірки поточного рядка.</summary>
    public object? GetCell(string columnCode);

    /// <summary>Значення комірки конкретного рядка таблиці.</summary>
    public object? GetCell(string rowKey, string columnCode);
}
