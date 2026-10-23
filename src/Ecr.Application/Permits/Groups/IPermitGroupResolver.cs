// src/Ecr.Application/Permits/Groups/IPermitGroupResolver.cs
using Ecr.Application.Security;

namespace Ecr.Application.Permits.Groups;

/// <summary>
/// ЄДИНИЙ порт «ціль Lookup → склад реєстрів»: відповідає, чи належить запис (за <c>RegistryDefId</c> його реєстру)
/// цілі колонки/поля, коли ціль - ГРУПА типів дозволів (прихований реєстр <c>PRMG_&lt;КОД&gt;</c>), і розгортає ціль
/// у склад реєстрів для читання (Permits, PRMG).
/// </summary>
/// <remarks>
/// <para>
/// <b>Модель.</b> Колонка/поле зберігає <c>LookupRegistryDefId</c> цілі. Для звичайної цілі (<c>LAND_*</c>, <c>PRM_&lt;TYPE&gt;</c>)
/// запис належить їй, коли <c>entry.RegistryDefId == target</c>. Для групи реєстр цілі ПОРОЖНІЙ (записів не має),
/// а запис - це дозвіл (запис <c>PRM_&lt;TYPE&gt;</c>) типу зі складу групи: у комірці лежить id запису реєстру-члена.
/// </para>
/// <para>
/// <b>Правило виклику (гарячий шлях).</b> Порівняння лишається <c>entry.RegistryDefId == target</c>. Лише при НЕзбігу:
/// <c>await EnsureFreshAsync(ct)</c> (один дешевий запит не частіше ніж раз на 5 с, інакше без I/O), далі
/// синхронний <see cref="Accepts"/>/<see cref="AcceptsFor"/>. Для Land (збіг реєстру) гілка не виконується -
/// 0 нових запитів. Усі методи крім <see cref="EnsureFreshAsync"/> СИНХРОННІ, без I/O, працюють зі знімка в памʼяті.
/// </para>
/// <para>
/// <b>Потокобезпека.</b> Реалізація (<c>PermitGroupResolver</c>) scoped, але знімок живе в singleton-кеші
/// (<c>PermitGroupSnapshotCache</c>): змінюється атомарною підміною посилання на незмінний обʼєкт, читачі
/// ніколи не бачать напівзібраного стану; методи безпечні для паралельного виклику.
/// </para>
/// <para>
/// <b>Інвалідація.</b> Створення/зміна складу/деактивація/видалення групи піднімає <c>DataRevision</c> прихованого реєстру
/// групи В ТІЙ САМІЙ транзакції; <see cref="EnsureFreshAsync"/> порівнює суму ревізій (один агрегатний запит) і
/// перечитує знімок лише при зміні (на всіх вузлах; TTL перевірки 5 с). На вузлі, що змінив склад, кеш скидається
/// одразу після коміту. Невідома ціль не змушує перечитувати знімок частіше за той самий 5-секундний інтервал
/// (негативного DoS через чужі id немає).
/// </para>
/// <para>
/// ⛔ Відмова для запису поза складом/чужого реєстру/реєстру з Deny має бути ТІЄЮ САМОЮ «немає запису» (AN-93,
/// <c>ECR-CELL-4223 missingEntry</c> / <c>ECR-HDR-0422 lookupEntryMissing</c>), що й для неіснуючого запису - інакше
/// різниця відповідей стає оракулом існування запису (клас S18). Deny блокує і ВИБІР запису (рішення людини 23.10), не
/// лише читання. Тому споживач, що має профіль, кличе <see cref="AcceptsFor"/> (для прямої цілі - через
/// <c>LookupTargetMatcher.Belongs</c>, який сам перевіряє Deny).
/// </para>
/// <para>
/// ⚠ Неактивна група: <see cref="IsGroup"/> лишається <c>true</c> (колонки не ламаються при читанні,
/// <see cref="ExpandRegistryIds"/>/<see cref="MemberRegistryIds"/> віддають склад), але <see cref="Accepts"/> і
/// <see cref="AcceptsFor"/> - <c>false</c>: нові значення відхиляються як «немає запису»; незмінені значення PATCH не
/// перевіряє. Тип, виключений зі складу, перестає проходити <see cref="Accepts"/>; уже обране значення лишається в комірці.
/// </para>
/// </remarks>
public interface IPermitGroupResolver
{
    /// <summary>
    /// Гарантує, що знімок не старший за 5 с і відповідає актуальній ревізії груп. Без I/O, коли знімок свіжий.
    /// Кликати ЛИШЕ на гілці незбігу реєстру (перед <see cref="Accepts"/>/<see cref="AcceptsFor"/>/
    /// <see cref="MemberRegistryIds"/>/<see cref="ExpandRegistryIds"/>/<see cref="IsGroup"/>), яка в Land недосяжна.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public ValueTask EnsureFreshAsync(CancellationToken ct);

    /// <summary>Чи є <paramref name="registryDefId"/> прихованим реєстром ГРУПИ (активної чи ні). Синхронно, зі знімка.</summary>
    /// <param name="registryDefId"><c>RegistryDefId</c> цілі.</param>
    public bool IsGroup(int registryDefId);

    /// <summary>
    /// Чи належить запис реєстру <paramref name="entryRegistryId"/> цілі <paramref name="targetRegistryId"/>:
    /// <c>true</c>, коли ціль - АКТИВНА група, а реєстр запису - реєстр шапки типу з її складу. Для цілі, що не група
    /// (і для невідомої групи), - <c>false</c>: збіг <c>entry == target</c> перевіряє споживач сам ДО виклику.
    /// </summary>
    /// <param name="targetRegistryId"><c>RegistryDefId</c> цілі колонки/поля (може бути <c>PRMG_*</c>).</param>
    /// <param name="entryRegistryId"><c>RegistryDefId</c> запису.</param>
    public bool Accepts(int targetRegistryId, int entryRegistryId);

    /// <summary>
    /// Те саме, що <see cref="Accepts"/>, плюс S18: <c>false</c>, коли для користувача реєстр запису під забороною
    /// (<c>RegistryAccess.IsDenied</c>). Відмова для споживача нерозрізнювана з «запису немає».
    /// </summary>
    /// <param name="profile">Профіль доступу користувача.</param>
    /// <param name="targetRegistryId"><c>RegistryDefId</c> цілі.</param>
    /// <param name="entryRegistryId"><c>RegistryDefId</c> запису.</param>
    public bool AcceptsFor(AccessProfile profile, int targetRegistryId, int entryRegistryId);

    /// <summary>
    /// Реєстри шапок типів складу групи в порядку складу; порожній для цілі, що не є групою (і для невідомої).
    /// Без I/O, зі знімка.
    /// </summary>
    /// <param name="groupRegistryId"><c>RegistryDefId</c> групи.</param>
    public IReadOnlyList<int> MemberRegistryIds(int groupRegistryId);

    /// <summary>
    /// Розгортання цілі в реєстри, з яких читаються записи (імпорт/експорт/слайси/синхронізація): для групи - склад
    /// (<see cref="MemberRegistryIds"/>), для будь-якої іншої цілі - <c>[targetRegistryId]</c> без I/O.
    /// </summary>
    /// <param name="targetRegistryId"><c>RegistryDefId</c> цілі колонки/поля.</param>
    public IReadOnlyList<int> ExpandRegistryIds(int targetRegistryId);
}

/// <summary>
/// Псевдонім <see cref="IPermitGroupResolver"/> під назвою з рев'ю «Аудиту» (<c>ILookupTargetResolver</c>): ОДИН порт, одна
/// реалізація, один екземпляр на scope; споживач бере будь-який з двох інтерфейсів.
/// </summary>
public interface ILookupTargetResolver : IPermitGroupResolver;
