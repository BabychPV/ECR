// src/Ecr.Domain/Entities/Calculations/MethodologyImport.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Оголошення: чиї формули видно виразам цієї версії через <c>!Name</c>
/// (директива ПК-1 №05, поправка 10).
/// </summary>
/// <remarks>
/// ⛔ Методологія **не замкнена**. У корпусі 149 посилань із <c>HSE400</c> і 116
/// з <c>Flert</c> ведуть у <c>Common</c>, а <c>ECW_C09_02_01</c> — одна формула
/// — потрібна п'яти методологіям. Резолвінг <c>!Name</c> лише «в межах своєї
/// версії» (02b §3.4) не знайшов би жодного з них: 265 посилань стали б
/// помилками публікації на цілком правильному корпусі.
/// <para>
/// ⚠ Імпорт називає **методологію**, а не версію. Версія бібліотеки
/// вибирається на дату періоду тим самим <c>Methodology.VersionOn</c>, що й
/// будь-яка інша: закріпити тут конкретну версію означало б, що перерахунок
/// минулого року бере сьогоднішню редакцію <c>Common</c> — рівно та тиха зміна
/// поданих чисел, від якої захищає ФВ-9.3.
/// </para>
/// <para>
/// ⚠ Порядку (<c>Ordinal</c>) тут немає **навмисно**. Він виглядав би як
/// пріоритет і мовчки розв'язував би неоднозначність: якби дві бібліотеки мали
/// формулу з тим самим кодом, перемагала б перша за списком, і число залежало б
/// від порядку рядків. Неоднозначність — помилка публікації, а не вибір.
/// </para>
/// <para>
/// ⚠ Імпортувати можна будь-яку методологію, не лише <c>Kind = Library</c>:
/// перехресне <c>!</c> — загальна можливість, а <c>Library</c> — опис природи,
/// не ворота (див. <see cref="Methodology.Kind"/>).
/// </para>
/// </remarks>
public sealed class MethodologyImport : Entity<int>
{
    private MethodologyImport() { }

    /// <summary>Оголошує імпорт.</summary>
    /// <param name="methodologyVersionId">Версія, якій потрібні чужі формули.</param>
    /// <param name="importedMethodologyId">Методологія, чиї формули стають видимими.</param>
    /// <param name="ownerMethodologyId">
    /// Методологія-власник версії — щоб відхилити самоімпорт.
    /// </param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0422</c> — методологія імпортує саму себе.
    /// </exception>
    /// <remarks>
    /// ⛔ Самоімпорт відхиляється: власні формули видно й без нього, а ребро
    /// «методологія → та сама методологія» в <c>calc.MethodologyDependency</c>
    /// топологічне сортування назве циклом — і перерахунок зупиниться на
    /// оголошенні, яке нічого не додавало.
    /// </remarks>
    public MethodologyImport(int methodologyVersionId, int importedMethodologyId, int ownerMethodologyId)
    {
        if (importedMethodologyId == ownerMethodologyId)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Методологія {ownerMethodologyId} імпортує саму себе: власні формули "
                + "видно виразам без оголошення.");
        }

        MethodologyVersionId = methodologyVersionId;
        ImportedMethodologyId = importedMethodologyId;
    }

    /// <summary>Версія, що оголошує імпорт.</summary>
    public int MethodologyVersionId { get; private set; }

    /// <summary>Методологія, чиї формули видно; версія добирається на дату періоду.</summary>
    public int ImportedMethodologyId { get; private set; }
}
