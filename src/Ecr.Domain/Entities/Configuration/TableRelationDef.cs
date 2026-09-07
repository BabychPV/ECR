using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Зв'язок між таблицями: дзеркало, rollup, посилання, каскад, перевірка, копія.</summary>
/// <remarks>
/// ⚠ Механізм **опційний** (<c>ФВ-2.12</c>): шаблон може не мати жодного
/// запису, і тоді його таблиці незалежні. Саме тому тут немає ані
/// обов'язкового зв'язку, ані «типового набору» — порожнеча є правильною
/// відповіддю, а не незаповненою конфігурацією.
///
/// ⛔ Налаштовується зв'язок **у вебі** (<c>ФВ-2.13</c>), тобто значення полів
/// приходять із мережі. Через це перевірки живуть тут, а не у формі: форма —
/// зручність, а домен — межа. Найдорожчі з них дві. Зв'язок таблиці саму із
/// собою СУБД відхиляє обмеженням <c>CK_Rel_NotSelf</c>, і без перевірки в
/// домені користувач діставав би не відмову форми, а помилку бази.
/// <see cref="OnSourceChange"/> — <c>tinyint</c> без обмеження в схемі, тобто
/// база прийняла б будь-яке значення до 255; невідомий код реакції потім
/// мовчки читався б як «нічого не робити», і зміна джерела перестала б
/// перераховувати приймач без жодного сліду.
/// </remarks>
public sealed class TableRelationDef : Entity<int>
{
    /// <summary>Перерахувати приймач при зміні джерела.</summary>
    public const byte OnSourceChangeRecalc = 0;

    /// <summary>Попередити людину, але правку прийняти.</summary>
    public const byte OnSourceChangeWarn = 1;

    /// <summary>Заблокувати зміну джерела.</summary>
    public const byte OnSourceChangeBlock = 2;

    private TableRelationDef() { }

    /// <summary>Створює зв'язок між двома таблицями версії.</summary>
    /// <param name="code">Код зв'язку; ним він адресується в API.</param>
    /// <param name="sourceTableDefId">Таблиця-джерело.</param>
    /// <param name="targetTableDefId">Таблиця-приймач.</param>
    /// <param name="kind">Вид зв'язку (<c>ФВ-2.12</c>).</param>
    /// <param name="matchJson">Як зіставляються рядки джерела і приймача.</param>
    /// <exception cref="DomainException">
    /// Таблиця пов'язується сама із собою або зіставлення порожнє.
    /// </exception>
    public TableRelationDef(EcrCode code, int sourceTableDefId, int targetTableDefId,
                            TableRelationKind kind, string matchJson)
    {
        Code = code.Value;
        Apply(sourceTableDefId, targetTableDefId, kind, matchJson, mapJson: null, OnSourceChangeRecalc);
        IsActive = true;
    }

    /// <summary>Код зв'язку — його ідентичність; після створення не змінюється.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Таблиця-джерело.</summary>
    public int SourceTableDefId { get; private set; }

    /// <summary>Таблиця-приймач.</summary>
    public int TargetTableDefId { get; private set; }

    /// <summary>Вид зв'язку.</summary>
    public TableRelationKind RelationKind { get; private set; }

    /// <summary>Як зіставляються рядки джерела і приймача.</summary>
    public string MatchJson { get; private set; } = null!;

    /// <summary>Які колонки на які.</summary>
    public string? MapJson { get; private set; }

    /// <summary>0 Recalc, 1 Warn, 2 Block — що робити при зміні джерела.</summary>
    public byte OnSourceChange { get; private set; }

    /// <summary>Чи діє зв'язок.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Перезаписує налаштування зв'язку.</summary>
    /// <param name="sourceTableDefId">Таблиця-джерело.</param>
    /// <param name="targetTableDefId">Таблиця-приймач.</param>
    /// <param name="kind">Вид зв'язку.</param>
    /// <param name="matchJson">Як зіставляються рядки.</param>
    /// <param name="mapJson">Які колонки на які; <c>null</c> — перенесення немає.</param>
    /// <param name="onSourceChange">Реакція на зміну джерела: 0 Recalc, 1 Warn, 2 Block.</param>
    /// <param name="isActive">Чи діє зв'язок.</param>
    /// <exception cref="DomainException">
    /// Таблиця пов'язується сама із собою, зіставлення порожнє або реакція
    /// на зміну джерела невідома.
    /// </exception>
    public void Update(int sourceTableDefId, int targetTableDefId, TableRelationKind kind,
                       string matchJson, string? mapJson, byte onSourceChange, bool isActive)
    {
        Apply(sourceTableDefId, targetTableDefId, kind, matchJson, mapJson, onSourceChange);
        IsActive = isActive;
    }

    /// <summary>Спільна перевірка для створення і правки.</summary>
    /// <param name="sourceTableDefId">Таблиця-джерело.</param>
    /// <param name="targetTableDefId">Таблиця-приймач.</param>
    /// <param name="kind">Вид зв'язку.</param>
    /// <param name="matchJson">Як зіставляються рядки.</param>
    /// <param name="mapJson">Які колонки на які.</param>
    /// <param name="onSourceChange">Реакція на зміну джерела.</param>
    /// <exception cref="DomainException">Будь-яка з перевірок не пройшла.</exception>
    private void Apply(int sourceTableDefId, int targetTableDefId, TableRelationKind kind,
                       string matchJson, string? mapJson, byte onSourceChange)
    {
        if (sourceTableDefId == targetTableDefId)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Зв'язок {Code} пов'язує таблицю {sourceTableDefId} саму із собою. " +
                "Це заборонено обмеженням CK_Rel_NotSelf: зіставляти рядки таблиці з її ж рядками " +
                "означає або тотожність, або цикл, і жодне з двох не є зв'язком.");
        }

        if (string.IsNullOrWhiteSpace(matchJson))
        {
            // ⛔ Зв'язок без зіставлення виглядає налаштованим і не з'єднує
            // жодного рядка. Це та сама мовчазна порожнеча, заради якої в
            // `PeriodAccessRuleDef` завели `CK_PAR_Kind`.
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Зв'язок {Code} не має зіставлення рядків (MatchJson). " +
                "Без нього він не з'єднує жодного рядка, лишаючись на вигляд налаштованим.");
        }

        if (onSourceChange > OnSourceChangeBlock)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Невідома реакція на зміну джерела: {onSourceChange}. " +
                $"Допустимі — {OnSourceChangeRecalc} Recalc, {OnSourceChangeWarn} Warn, " +
                $"{OnSourceChangeBlock} Block.");
        }

        SourceTableDefId = sourceTableDefId;
        TargetTableDefId = targetTableDefId;
        RelationKind = kind;
        MatchJson = matchJson;
        MapJson = string.IsNullOrWhiteSpace(mapJson) ? null : mapJson;
        OnSourceChange = onSourceChange;
    }
}
