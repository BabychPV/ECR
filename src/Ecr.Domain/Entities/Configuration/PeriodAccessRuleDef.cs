using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Правило доступу до періоду — заміна кнопки <c>Protect</c> чинного рішення
/// (ФВ-2.15): який аркуш, таблиця або рядок у якому періоді доступні на
/// введення.
/// </summary>
/// <remarks>
/// ⛔ Шість видів правил (<see cref="PeriodAccessRuleKind"/>), а не один. До
/// цього тут жив лише статичний діапазон номерів періодів
/// (<see cref="FromSequence"/>…<see cref="ToSequence"/>) — тобто з шести
/// механізмів чинного рішення переносився один. Разом із рештою зникало те
/// єдине, що потрібне <c>ФВ-5.20</c>: вікно, взяте з довідника.
///
/// ⚠ Створити правило можна ТІЛЬКИ фабричним методом. Публічного
/// конструктора немає навмисно: у кожного виду свій обов'язковий параметр, і
/// «створив і забув заповнити» тут означає правило, яке мовчки нічого не
/// робить. База ловить це <c>CK_PAR_Kind</c>, але база — остання лінія, а не
/// перша.
/// </remarks>
public sealed class PeriodAccessRuleDef : Entity<int>
{
    private PeriodAccessRuleDef() { }

    private PeriodAccessRuleDef(
        int templateVersionId, PeriodAccessRuleKind kind, OutOfWindowBehavior onOutOfWindow)
    {
        TemplateVersionId = templateVersionId;
        RuleKind = kind;
        OnOutOfWindow = onOutOfWindow;
    }

    public int TemplateVersionId { get; private set; }

    /// <summary>Вид правила; визначає, який саме параметр обов'язковий.</summary>
    public PeriodAccessRuleKind RuleKind { get; private set; }

    public int? SheetDefId { get; private set; }
    public int? TableDefId { get; private set; }

    /// <summary><c>null</c> — правило діє для всіх ролей.</summary>
    public int? RoleId { get; private set; }

    /// <summary>
    /// Вид рядків, на які поширюється правило; <c>null</c> — на всі.
    /// </summary>
    /// <remarks>
    /// Потрібен <see cref="PeriodAccessRuleKind.HeaderRows"/> і правилам виду
    /// «формульні підсумки лише для читання»: у чинному рішенні це масиви
    /// номерів рядків, які розсипалися при кожній вставці рядка.
    /// </remarks>
    public RowKind? RowKind { get; private set; }

    /// <summary>Від якого порядкового номера періоду доступно; <c>null</c> — без обмеження.</summary>
    public byte? FromSequence { get; private set; }

    public byte? ToSequence { get; private set; }

    /// <summary>
    /// Колонка, з якої береться запис довідника для
    /// <see cref="PeriodAccessRuleKind.SourceWindow"/>.
    /// </summary>
    public int? SourceColumnDefId { get; private set; }

    /// <summary>Зсув ±N періодів від поточного для <see cref="PeriodAccessRuleKind.RelativeWindow"/>.</summary>
    public short? RelativeOffset { get; private set; }

    /// <summary>Булевий вираз діалекту шаблонів для <see cref="PeriodAccessRuleKind.Expression"/>.</summary>
    public string? ConditionExpr { get; private set; }

    public OutOfWindowBehavior OnOutOfWindow { get; private set; }

    /// <summary>Завжди лише читання.</summary>
    public static PeriodAccessRuleDef AlwaysReadOnly(
        int templateVersionId, OutOfWindowBehavior onOutOfWindow = OutOfWindowBehavior.ReadOnly)
        => new(templateVersionId, PeriodAccessRuleKind.AlwaysReadOnly, onOutOfWindow);

    /// <summary>Рядки-заголовки заблоковані.</summary>
    public static PeriodAccessRuleDef HeaderRows(
        int templateVersionId, OutOfWindowBehavior onOutOfWindow = OutOfWindowBehavior.ReadOnly)
    {
        var rule = new PeriodAccessRuleDef(
            templateVersionId, PeriodAccessRuleKind.HeaderRows, onOutOfWindow);

        rule.RowKind = Enums.RowKind.Header;

        return rule;
    }

    /// <summary>Редагується лише період у заданому діапазоні номерів.</summary>
    public static PeriodAccessRuleDef EditablePeriodOnly(
        int templateVersionId,
        OutOfWindowBehavior onOutOfWindow,
        byte? fromSequence = null,
        byte? toSequence = null)
    {
        var rule = new PeriodAccessRuleDef(
            templateVersionId, PeriodAccessRuleKind.EditablePeriodOnly, onOutOfWindow);

        rule.FromSequence = fromSequence;
        rule.ToSequence = toSequence;

        return rule;
    }

    /// <summary>Вікно «поточний період ± N».</summary>
    /// <exception cref="DomainException">Зсув від'ємний або нульовий.</exception>
    public static PeriodAccessRuleDef ForRelativeWindow(
        int templateVersionId, short offset, OutOfWindowBehavior onOutOfWindow)
    {
        // ⚠ Нуль означав би «лише поточний період», і це вже
        // `EditablePeriodOnly`. Два імені однієї поведінки розходяться на
        // першій же правці.
        if (offset <= 0)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Зсув відносного вікна має бути додатним; отримано {offset}. "
                + "Нуль — це EditablePeriodOnly, і його треба задавати саме ним.");
        }

        var rule = new PeriodAccessRuleDef(
            templateVersionId, PeriodAccessRuleKind.RelativeWindow, onOutOfWindow);

        rule.RelativeOffset = offset;

        return rule;
    }

    /// <summary>
    /// Вікно береться з довідника: дати дії запису, на який посилається рядок
    /// (<c>ФВ-5.20</c>).
    /// </summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="sourceColumnDefId">Колонка типу <c>Lookup</c> із посиланням на запис.</param>
    /// <param name="onOutOfWindow">Поведінка поза вікном.</param>
    public static PeriodAccessRuleDef ForSourceWindow(
        int templateVersionId, int sourceColumnDefId, OutOfWindowBehavior onOutOfWindow)
    {
        var rule = new PeriodAccessRuleDef(
            templateVersionId, PeriodAccessRuleKind.SourceWindow, onOutOfWindow);

        rule.SourceColumnDefId = sourceColumnDefId;

        return rule;
    }

    /// <summary>Довільна умова над значеннями рядка.</summary>
    /// <exception cref="DomainException">Вираз порожній.</exception>
    public static PeriodAccessRuleDef ForExpression(
        int templateVersionId, string condition, OutOfWindowBehavior onOutOfWindow)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                "Правило виду Expression без виразу не робить нічого; "
                + "порожня умова тут — те саме, що відсутнє правило.");
        }

        var rule = new PeriodAccessRuleDef(
            templateVersionId, PeriodAccessRuleKind.Expression, onOutOfWindow);

        rule.ConditionExpr = condition;

        return rule;
    }

    /// <summary>Обмежує правило аркушем.</summary>
    public PeriodAccessRuleDef ForSheet(int sheetDefId)
    {
        SheetDefId = sheetDefId;

        return this;
    }

    /// <summary>Обмежує правило таблицею.</summary>
    public PeriodAccessRuleDef ForTable(int tableDefId)
    {
        TableDefId = tableDefId;

        return this;
    }

    /// <summary>Обмежує правило видом рядків.</summary>
    public PeriodAccessRuleDef ForRows(RowKind rowKind)
    {
        RowKind = rowKind;

        return this;
    }

    /// <summary>Обмежує правило роллю.</summary>
    public PeriodAccessRuleDef ForRole(int roleId)
    {
        RoleId = roleId;

        return this;
    }

    /// <summary>Чи діє правило для періоду з таким порядковим номером.</summary>
    public bool AppliesTo(byte sequence)
        => (FromSequence is null || sequence >= FromSequence)
        && (ToSequence is null || sequence <= ToSequence);
}
