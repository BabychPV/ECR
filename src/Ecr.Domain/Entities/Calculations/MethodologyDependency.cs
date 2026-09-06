// src/Ecr.Domain/Entities/Calculations/MethodologyDependency.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Ребро графа залежностей **між методологіями** (`B13` §4.3):
/// <c>From</c> читає результат <c>To</c>, отже рахується після нього.
/// </summary>
/// <remarks>
/// ⛔ У чинній системі цей порядок заданий неявно — каденцією джобів
/// (<c>HSE400</c> перший, <c>Thermaloxidizer</c> останній). Тут він явний, і
/// саме тому ребро <b>обов'язково будується і для посилань у бібліотеку</b>
/// (директива ПК-1 №05, поправка 10): без нього топологічний порядок
/// перерахунку неповний, і методологія читає торішній результат
/// <c>Common</c> — число правдоподібне, помилки немає, звірка нічого не
/// показує.
/// <para>
/// ⚠ Ребро на рівні МЕТОДОЛОГІЙ, а не версій: версія кожної вибирається на
/// дату періоду окремо (ФВ-9.3), і ребро між версіями довелося б
/// перебудовувати щоразу, коли будь-яка з них публікує наступну.
/// </para>
/// </remarks>
public sealed class MethodologyDependency : Entity<int>
{
    private MethodologyDependency() { }

    /// <summary>Створює ребро.</summary>
    /// <param name="fromMethodologyId">Хто посилається — рахується пізніше.</param>
    /// <param name="toMethodologyId">На кого — рахується раніше.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0422</c> — петля: методологія залежить від себе.
    /// </exception>
    /// <remarks>
    /// ⛔ Петля відхиляється тут, бо в топологічному сортуванні вона стає
    /// «циклом» без жодної підказки, у чому річ, і зупиняє весь перерахунок,
    /// а не одну методологію.
    /// </remarks>
    public MethodologyDependency(int fromMethodologyId, int toMethodologyId)
    {
        if (fromMethodologyId == toMethodologyId)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Методологія {fromMethodologyId} не може залежати від самої себе: "
                + "ребро-петля зупиняє топологічний порядок усього перерахунку.");
        }

        FromMethodologyId = fromMethodologyId;
        ToMethodologyId = toMethodologyId;
    }

    /// <summary>Методологія, що посилається.</summary>
    public int FromMethodologyId { get; private set; }

    /// <summary>Методологія, на яку посилаються.</summary>
    public int ToMethodologyId { get; private set; }
}
