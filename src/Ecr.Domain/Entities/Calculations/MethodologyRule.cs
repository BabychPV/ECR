// src/Ecr.Domain/Entities/Calculations/MethodologyRule.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Правило прив'язки методології до рядків документа (ФВ-13.3, ФВ-13.8):
/// **умова**, а не жорсткий список <c>RowKey</c>.
/// </summary>
/// <remarks>
/// Правила впорядковані за <see cref="Priority"/>, **перший збіг виграє**
/// (ФВ-13.4). На тому самому механізмі будується матриця покриття: рядок, який
/// не зачепило жодне правило, видно до публікації, а не за розбіжністю в звіті.
///
/// ⚠ <b>Q-026.</b> У скелеті <c>05b</c> поле предиката звалося
/// <c>ConditionExpression</c> і описувалося як «умова діалекту методологій».
/// Це неможливо: діалект <c>Methodology</c> посилань на комірки документів
/// <b>не має</b> (<c>02b</c> §3.4) — він працює з підготовленими аргументами,
/// а зіставляти треба саме рядки документа. Третій діалект під це не
/// створюється (<c>D-92</c>: «система будується на двох діалектах і одному
/// парсері», <c>ФВ-9.5</c>). Тому предикат — <b>структурований</b>, як і в
/// решті пакета: <c>cfg.TableRelationDef.MatchJson</c>,
/// <c>cfg.CalculationBinding.MatchJson</c>, <c>cfg.FormulaDependency.FilterJson</c>.
/// Поле приведено до схеми <c>calc.MethodologyRule</c> (<c>02a</c> рядок 1069).
/// </remarks>
public sealed class MethodologyRule : Entity<int>
{
    private MethodologyRule() { }

    /// <summary>Створює правило.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код правила, унікальний у межах версії.</param>
    /// <param name="matchJson">Структурований предикат зіставлення рядків.</param>
    /// <param name="priority">Пріоритет; менше значення — вищий.</param>
    public MethodologyRule(int methodologyVersionId, EcrCode code, string matchJson, int priority)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        MatchJson = matchJson;
        Priority = priority;
        IsActive = true;
    }

    /// <summary>Версія методології, якій належить правило.</summary>
    public int MethodologyVersionId { get; private set; }

    /// <summary>
    /// Код правила. Обов'язковий і унікальний у межах версії
    /// (<c>UQ_MethodologyRule (MethodologyVersionId, Code)</c>): перевірки
    /// перетину і покриття при публікації (ФВ-13.9) мають назвати, <b>яке саме</b>
    /// правило конфліктує, а не «одне з правил».
    /// </summary>
    public string Code { get; private set; } = null!;

    /// <summary>
    /// Структурований предикат зіставлення: посилається на <b>реєстри й
    /// атрибути</b>, а не на конкретні <c>RowKey</c> (ФВ-13.8).
    /// </summary>
    public string MatchJson { get; private set; } = null!;

    /// <summary>Менше значення — вищий пріоритет. Перший збіг виграє.</summary>
    public int Priority { get; private set; }

    /// <summary>Неактивне правило не бере участі ні в зіставленні, ні в матриці покриття.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Вмикає або вимикає правило.</summary>
    /// <param name="isActive">Чи бере правило участь у зіставленні.</param>
    /// <remarks>
    /// ⛔ Метод з'явився не заради екрана правил (його ще немає), а заради
    /// **клону версії**. Конструктор ставить <c>IsActive = true</c>, і клон,
    /// який не має чим повернути <c>false</c>, вмикав би вимкнене правило —
    /// тобто тихо змінював би те, ЩО ВЗАГАЛІ рахується (ФВ-13.4), у версії,
    /// зробленій «щоб нічого не змінювати».
    /// </remarks>
    public void SetActive(bool isActive) => IsActive = isActive;
}
