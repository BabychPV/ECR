using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Правило валідації. Рівень визначає, чи блокує воно запис (R-B3).</summary>
public sealed class ValidationRule : Entity<int>
{
    private ValidationRule() { }

    /// <param name="tableDefId">Таблиця, якій належить правило.</param>
    /// <param name="code">Код правила; він же в повідомленні порушення.</param>
    /// <param name="severity">Рівень: <c>Info</c>, <c>Warning</c>, <c>Error</c>.</param>
    /// <param name="scope">0 Cell, 1 Row, 2 Table, 3 Document.</param>
    /// <param name="expression">Предикат нашою мовою.</param>
    /// <param name="message">Текст порушення мовами каталогу.</param>
    /// <param name="columnDefId">
    /// Колонка, до якої прив'язане правило; <c>null</c> — до всіх колонок
    /// таблиці.
    /// </param>
    /// <remarks>
    /// ⚠ <paramref name="columnDefId"/> зʼявився з аудиту: властивість
    /// існувала, рушій валідації її читав — і не було
    /// жодного способу її задати. Тобто гілка «правило лише для цієї
    /// колонки» була недосяжна, і кожне коміркове правило застосовувалося
    /// до всієї таблиці.
    /// </remarks>
    public ValidationRule(int tableDefId, EcrCode code, ValidationSeverity severity, byte scope,
                          string expression, LocalizedText message, int? columnDefId = null)
    {
        TableDefId = tableDefId;
        Code = code.Value;
        Severity = severity;
        Scope = scope;
        ColumnDefId = columnDefId;
        Expression = expression;
        MessageL10n = message;
        IsActive = true;
    }

    public int TableDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public ValidationSeverity Severity { get; private set; }

    /// <summary>0 Cell, 1 Row, 2 Table, 3 Document.</summary>
    public byte Scope { get; private set; }

    public int? ColumnDefId { get; private set; }
    public string Expression { get; private set; } = null!;
    public LocalizedText MessageL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    /// <summary>
    /// Чи блокує правило збереження. Блокує **лише** комірковий <c>Error</c>:
    /// заборона зберегти проміжний стан робить роботу з великою таблицею
    /// неможливою (R-B3).
    /// </summary>
    public bool BlocksSave => Severity == ValidationSeverity.Error && Scope == 0;

    /// <summary>Перезаписує налаштування правила.</summary>
    /// <remarks>
    /// ⛔ Сеттер потрібен для <c>PUT …/validation-rules/{code}</c>
    /// (авторство структури шаблону, W5.4, продовження зрізу ФВ-2.1..ФВ-2.5
    /// на <c>ValidationRule</c>): до цього кожне поле, крім <see cref="Code"/>
    /// і <see cref="TableDefId"/> (адреса й батько — не змінюються), задавав
    /// лише конструктор, і повторний виклик тим самим кодом не мав як
    /// оновити наявне правило.
    ///
    /// ⚠ <see cref="Code"/> і <see cref="TableDefId"/> тут НЕ змінюються
    /// навмисно — так само, як <see cref="SheetDef.Code"/> в
    /// <see cref="SheetDef.Rename"/>: код — це ідентичність і адреса в API,
    /// а таблиця-батько — те, через що правило взагалі знайшли.
    /// </remarks>
    /// <param name="severity">Рівень: <c>Info</c>, <c>Warning</c>, <c>Error</c>.</param>
    /// <param name="scope">0 Cell, 1 Row, 2 Table, 3 Document.</param>
    /// <param name="expression">Предикат нашою мовою.</param>
    /// <param name="message">Текст порушення мовами каталогу.</param>
    /// <param name="columnDefId">Колонка, до якої прив'язане правило; <c>null</c> — до всіх колонок таблиці.</param>
    /// <param name="isActive">Чи діє правило.</param>
    public void Update(
        ValidationSeverity severity, byte scope, string expression, LocalizedText message,
        int? columnDefId, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        Severity = severity;
        Scope = scope;
        ColumnDefId = columnDefId;
        Expression = expression;
        MessageL10n = message;
        IsActive = isActive;
    }
}
