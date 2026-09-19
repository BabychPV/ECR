// src/Ecr.Application/Documents/Dto/CellConflictDto.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Конфлікт паралельного редагування. Повертається в
/// <c>Extensions2.conflicts</c> при <c>ECR-CELL-0409</c>.
/// «Перезаписати мовчки» не є опцією: користувач має побачити розбіжність.
/// </summary>
/// <remarks>
/// ⛔ <c>BE-06</c>. Три поля («чиє значення, хто, коли») існували й ДО цієї
/// роботи — і заповнювалися заглушками: <c>null</c>, <c>""</c> і
/// <c>clock.UtcNow</c>. Це гірше за відсутність поля: діалог конфлікту показав
/// би порожнечу й ПОТОЧНИЙ ЧАС СЕРВЕРА як момент чужої правки, а користувач
/// вирішує «беру їхнє / лишаю своє» саме за цим числом.
///
/// ⚠ Тому всі три тепер <b>необов'язкові</b>: <c>null</c> означає «невідомо», і
/// це чесна відповідь там, де адреси комірки немає (<c>ColumnCode = *</c>) або
/// у вікні журналу змін не знайшлося. Підставити замість неї правдоподібне
/// число — та сама помилка, лише акуратніше записана.
/// </remarks>
/// <param name="RowKey">Ключ рядка, чия версія розійшлася.</param>
/// <param name="ColumnCode">
/// Колонка; <c>*</c> — розійшовся весь рядок, конкретної колонки назвати не можна.
/// </param>
/// <param name="YourValue">Значення, яке надіслав цей користувач.</param>
/// <param name="TheirValue">
/// Чинне значення комірки — те, що лежить у сховищі зараз; <c>null</c> —
/// комірки немає або назвати її неможливо.
/// </param>
/// <param name="TheirUser">
/// Відображуване ім'я автора останньої зміни; <c>system</c> — зміна не людини
/// (перерахунок, імпорт, міграція); <c>null</c> — автор невідомий. Ніколи не
/// логін і не SID (R-A2, D-86).
/// </param>
/// <param name="TheirOrigin">
/// Походження останньої зміни: <c>UserEdit</c>, <c>Import</c>,
/// <c>Recalculation</c>, <c>Migration</c>; <c>null</c> — невідоме. Потрібне
/// поруч із <c>system</c>: «це зробила не людина» без відповіді «а що саме»
/// лишає користувача з тим самим питанням.
/// </param>
/// <param name="TheirChangedAt">
/// Момент останньої зміни в UTC; <c>null</c> — невідомо.
/// </param>
/// <param name="CurrentVersion">Чинна версія рядка.</param>
public sealed record CellConflictDto(
    string RowKey,
    string ColumnCode,
    object? YourValue,
    object? TheirValue,
    string? TheirUser,
    string? TheirOrigin,
    DateTime? TheirChangedAt,
    string CurrentVersion);
