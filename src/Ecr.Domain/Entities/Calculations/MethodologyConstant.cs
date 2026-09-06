// src/Ecr.Domain/Entities/Calculations/MethodologyConstant.cs
using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Константа методології: щільність, теплотворність, молярна маса, коефіцієнт
/// емісії. Для числової константи **одиниця обов'язкова** (ФВ-16.1).
/// </summary>
/// <remarks>
/// ⚠ Саме тут живуть **контекстні коефіцієнти**, а не в <c>uom.Conversion</c>
/// (ФВ-16.5, D-75). Щільність води — не конверсія «м³ → кг»: вона залежить від
/// температури, а для нафти взагалі інша. Спроба покласти таке в таблицю
/// конверсій відхиляється базою (<c>ECR-UOM-4221</c>).
/// <para>
/// ⛔ **Константа не завжди число** (директива ПК-1 №05, поправка 2-біс). З 6507
/// констант корпусу 108 нечислові, і ~90 із них ужиті у виразах — як операнд
/// порівняння. Правило «нечислове значення — помилка публікації», записане в
/// пакеті, було перевернуте: воно відхилило б усі 108 і зламало б механізм
/// категорій. Тому <see cref="Kind"/>, а не заборона.
/// </para>
/// </remarks>
public sealed class MethodologyConstant : Entity<int>
{
    private MethodologyConstant() { }

    /// <summary>Створює числову константу (<see cref="ConstantKind.Numeric"/>).</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код константи — те, що стоїть після <c>CST.</c>.</param>
    /// <param name="value">Значення.</param>
    /// <param name="unitId">Одиниця; для числа обов'язкова (ФВ-16.1).</param>
    public MethodologyConstant(int methodologyVersionId, EcrCode code, decimal value, int unitId)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        Kind = ConstantKind.Numeric;
        Value = value;
        UnitId = unitId;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;

    /// <summary>Природа значення: число, текст або мітка категорії.</summary>
    public ConstantKind Kind { get; private set; }

    /// <summary>
    /// Число; <c>null</c> для тексту й мітки, а також для рядка, який імпорт не
    /// зміг розібрати.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>decimal?</c>, а не <c>decimal</c>, саме заради третього випадку.
    /// Ненульовий тип змусив би записати сюди нуль — і три відомі дефекти
    /// корпусу (<c>n_ECW_C11_13_ = '-'</c> у 16 формулах,
    /// <c>Kp_ECW_C11_13_ = '-'</c> у 4, <c>k22_HSE30X_Int_FG_ = ''</c> у 5)
    /// стали б тихими нулями в числах звіту замість переліку на екрані
    /// публікації.
    /// </remarks>
    public decimal? Value { get; private set; }

    /// <summary>
    /// Текстове значення: сам текст для <see cref="ConstantKind.Text"/> і
    /// <see cref="ConstantKind.CategoryLabel"/>, сирий рядок джерела — для
    /// нерозібраного числа.
    /// </summary>
    public string? TextValue { get; private set; }

    /// <summary>Одиниця; <c>null</c> для тексту й мітки — вимір у них не має сенсу.</summary>
    public int? UnitId { get; private set; }

    public DateOnly? ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public long? SubstanceEntryId { get; private set; }

    /// <summary>
    /// Категорія застосування: <c>default</c>, <c>offshore</c>, назва
    /// установки. <c>null</c> — константа спільна для всіх.
    /// </summary>
    /// <remarks>
    /// ⚠ Разом із <see cref="SubstanceEntryId"/> утворює ключ звуження:
    /// точний збіг виграє над загальним. Без категорії довелося б заводити
    /// окрему методологію на кожну установку — саме те, від чого система
    /// відходить (`calc`-частина `Q-027`).
    /// </remarks>
    public string? Category { get; private set; }

    /// <summary>Звідки взято значення: наказ, паспорт установки, вимірювання.</summary>
    /// <remarks>
    /// Не метадані «для порядку»: коефіцієнт емісії без джерела неможливо ні
    /// захистити перед регулятором, ні оновити, коли документ перевидадуть.
    /// </remarks>
    public string? Source { get; private set; }

    /// <summary>
    /// Чи придатна константа до підстановки у вираз узагалі.
    /// </summary>
    /// <remarks>
    /// ⛔ Мітка категорії — ні. Вона існує тільки для вибору категорії
    /// (<c>k1_CategorySelection_ = 'Summer'</c> як ЗНАЧЕННЯ, за яким звужується
    /// набір констант), і підстановка її у вираз означає, що методолог
    /// переплутав ключ звуження зі значенням.
    /// </remarks>
    public bool IsAllowedInExpression => Kind != ConstantKind.CategoryLabel;

    /// <summary>
    /// Чи готова константа до обчислення: число розібране, текст непорожній.
    /// </summary>
    /// <remarks>
    /// ⛔ Саме це і є «рішення, а не тихий нуль»: <c>Kind = Numeric</c> без
    /// <see cref="Value"/> — рядок, який імпорт зберіг як є, і публікація
    /// зобов'язана його назвати.
    /// </remarks>
    public bool IsResolved => Kind == ConstantKind.Numeric
        ? Value is not null
        : !string.IsNullOrWhiteSpace(TextValue);

    /// <summary>Створює текстову константу або мітку категорії.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код константи.</param>
    /// <param name="text">Текст; порожній рядок і пробіли не приймаються.</param>
    /// <param name="kind">
    /// <see cref="ConstantKind.Text"/> або <see cref="ConstantKind.CategoryLabel"/>.
    /// </param>
    /// <returns>Нову константу.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0422</c> — вид числовий (для нього є конструктор) або текст порожній.
    /// </exception>
    /// <remarks>
    /// ⛔ Порожній текст відхиляється тут, а не мовчки зберігається:
    /// <c>k22_HSE30X_Int_FG_ = ''</c> ужита в 5 формулах, і порожній рядок не є
    /// ні міткою, ні числом. Прийняти його означало б, що п'ять формул рахують
    /// із порожнечею і ніхто про це не дізнається.
    /// </remarks>
    public static MethodologyConstant OfText(
        int methodologyVersionId, EcrCode code, string text, ConstantKind kind)
    {
        if (kind == ConstantKind.Numeric)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Константа «{code.Value}»: вид {kind} створюється конструктором зі значенням decimal.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Константа «{code.Value}» виду {kind} без тексту: порожній рядок не є "
                + "ні міткою категорії, ні значенням (поправка 2-біс директиви ПК-1 №05).");
        }

        return new MethodologyConstant
        {
            MethodologyVersionId = methodologyVersionId,
            Code = code.Value,
            Kind = kind,
            TextValue = text,
        };
    }

    /// <summary>
    /// Створює константу з **сирого** рядка джерела (<c>CInfo_Value</c>).
    /// </summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код константи.</param>
    /// <param name="rawValue">Значення як воно записане в джерелі.</param>
    /// <param name="kind">Вид, визначений імпортером (див. <see cref="ClassifyText"/>).</param>
    /// <param name="unitId">Одиниця; для <see cref="ConstantKind.Numeric"/> обов'язкова.</param>
    /// <returns>Нову константу; нерозібране число лишається нерозібраним.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0422</c> — числовий вид без одиниці, або текстовий вид із порожнім рядком.
    /// </exception>
    /// <remarks>
    /// ⛔ Нерозібраний рядок при <c>Kind = Numeric</c> — **не** виняток імпорту.
    /// Він зберігається як є (<see cref="TextValue"/>) з порожнім
    /// <see cref="Value"/> і доїжджає до публікації, де стає рядком у переліку
    /// проблем. Кинути тут означало б, що імпорт корпусу падає на трьох
    /// відомих дефектах і жодного з решти 6504 рядків не переносить; підставити
    /// нуль — що <c>n_ECW_C11_13_ = '-'</c> мовчки обнуляє 16 формул.
    /// </remarks>
    public static MethodologyConstant FromImport(
        int methodologyVersionId, EcrCode code, string? rawValue, ConstantKind kind, int? unitId)
    {
        if (kind != ConstantKind.Numeric)
        {
            return OfText(methodologyVersionId, code, rawValue ?? string.Empty, kind);
        }

        if (unitId is not { } unit)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Числова константа «{code.Value}» без одиниці: перевірка розмірностей "
                + "без неї неможлива (ФВ-16.1).");
        }

        if (TryParseNumeric(rawValue, out var parsed))
        {
            return new MethodologyConstant(methodologyVersionId, code, parsed, unit);
        }

        return new MethodologyConstant
        {
            MethodologyVersionId = methodologyVersionId,
            Code = code.Value,
            Kind = ConstantKind.Numeric,
            Value = null,
            TextValue = rawValue,
            UnitId = unit,
        };
    }

    /// <summary>
    /// Розбирає значення числової константи **інваріантно**.
    /// </summary>
    /// <param name="rawValue">Сирий рядок джерела.</param>
    /// <param name="value">Розібране число; нуль, якщо не розібралося.</param>
    /// <returns><c>true</c>, якщо рядок є числом.</returns>
    /// <remarks>
    /// ⚠ Інваріантна культура — не формальність: у джерелі роздільник дробової
    /// частини завжди крапка (02b §5), а на машині з українською локаллю
    /// <c>0.85</c> розібралося б як 85.
    /// </remarks>
    public static bool TryParseNumeric(string? rawValue, out decimal value)
        => decimal.TryParse(
            rawValue,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Вид нечислової константи — **за вживанням**.
    /// </summary>
    /// <param name="referencedByAnyExpression">
    /// Чи посилається на цю константу бодай один вираз (<c>FInfo_Text</c>).
    /// </param>
    /// <returns>
    /// <see cref="ConstantKind.Text"/>, якщо посилається; інакше
    /// <see cref="ConstantKind.CategoryLabel"/>.
    /// </returns>
    /// <remarks>
    /// ⚠ Іншої ознаки в джерелі не існує: у <c>CInfo</c> текст і мітка категорії
    /// лежать в одній колонці й виглядають однаково (<c>'Summer'</c>,
    /// <c>'&lt;1500'</c>, <c>'LPG - СУГ'</c>). Розрізняє їх тільки те, чи
    /// згадана константа у виразі. Метод оголошений у домені, щоб імпортер
    /// (він пишеться окремо) не завів другого, розбіжного правила.
    /// </remarks>
    public static ConstantKind ClassifyText(bool referencedByAnyExpression)
        => referencedByAnyExpression ? ConstantKind.Text : ConstantKind.CategoryLabel;

    /// <summary>Звужує застосування константи.</summary>
    /// <param name="category">Категорія; <c>null</c> — спільна.</param>
    /// <param name="substanceEntryId">Речовина; <c>null</c> — спільна.</param>
    public void SetScope(string? category, long? substanceEntryId)
    {
        Category = category;
        SubstanceEntryId = substanceEntryId;
    }

    /// <summary>Задає вікно чинності.</summary>
    /// <param name="from">Початок; <c>null</c> — від початку.</param>
    /// <param name="to">Кінець; <c>null</c> — без обмеження.</param>
    /// <exception cref="DomainException">Порожнє вікно — <c>ECR-CALC-0422</c>.</exception>
    public void SetValidity(DateOnly? from, DateOnly? to)
    {
        if (from is { } start && to is { } end && end < start)
        {
            throw new DomainException(
                "ECR-CALC-0422", $"Кінець вікна чинності {end} раніший за початок {start}.");
        }

        ValidFrom = from;
        ValidTo = to;
    }

    /// <summary>Записує джерело значення.</summary>
    /// <param name="source">Опис джерела.</param>
    public void SetSource(string? source) => Source = source;
}
