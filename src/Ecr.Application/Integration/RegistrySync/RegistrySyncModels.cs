// src/Ecr.Application/Integration/RegistrySync/RegistrySyncModels.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.RegistrySync;

/// <summary>
/// Елемент зовнішнього джерела (елемент PI AF) у знімку, прочитаному для
/// синхронізації довідника (<c>ФВ-8.11</c>).
/// </summary>
/// <param name="ExternalId">Технічний ідентифікатор елемента в джерелі (WebId/GUID AF).</param>
/// <param name="ExternalPath">Шлях в ієрархії джерела; <c>null</c> — джерело його не віддало.</param>
/// <param name="Attributes">
/// Значення атрибутів за іменем атрибута джерела (<see cref="RegistrySyncFieldMapping.SourceAttribute"/>).
/// ⚠ Атрибута НЕМАЄ в словнику — джерело нічого про нього не сказало, поле не
/// чіпається; атрибут є зі значенням <c>null</c> — джерело каже «порожньо».
/// Одиниці вже приведені до одиниці поля на межі (<c>D-173</c>) — планувальник їх не конвертує.
/// </param>
public sealed record RegistrySyncSourceElement(
    string ExternalId,
    string? ExternalPath,
    IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// Наявний зв'язок «елемент джерела → запис довідника»: рядок
/// <c>dic.RegistryExternalKey</c> (<c>ФВ-8.10</c>).
/// </summary>
/// <param name="ExternalId">Ідентифікатор елемента в джерелі.</param>
/// <param name="RegistryEntryId">Запис довідника.</param>
/// <param name="ExternalPath">Шлях, збережений під час останньої синхронізації.</param>
public sealed record RegistrySyncLink(
    string ExternalId,
    long RegistryEntryId,
    string? ExternalPath);

/// <summary>Поточне значення одного поля запису довідника.</summary>
/// <param name="Value">
/// Типізоване значення в тій формі, яку зберігає <c>dic.RegistryValue</c>:
/// <c>decimal</c> для <c>Int</c>/<c>Decimal</c>, <c>string</c>, <c>bool</c>,
/// <c>DateTime</c>, <c>long</c> для <c>Lookup</c>, <c>int</c> для <c>Unit</c>.
/// </param>
/// <param name="LastWriterIsHuman">
/// Останнім значення записала людина, а не <c>svc-integration</c> — тоді синк
/// довідника <c>Hybrid</c> його не перетирає (<c>D-118</c>). ⚠ Для <c>External</c>
/// ознака не діє: ручного запису там немає (<c>D-211</c>, <c>D-212</c> (1)), і
/// значення «від людини» — це залишок до блокування, який синк перезаписує.
/// </param>
public sealed record RegistrySyncCurrentValue(object? Value, bool LastWriterIsHuman);

/// <summary>Поточний стан запису довідника, прив'язаного до елемента джерела.</summary>
/// <param name="RegistryEntryId">Запис довідника.</param>
/// <param name="Values">Поточні значення за <c>RegistryFieldDefId</c>; поля без значення можна не передавати.</param>
public sealed record RegistrySyncEntryState(
    long RegistryEntryId,
    IReadOnlyDictionary<int, RegistrySyncCurrentValue> Values);

/// <summary>Мапінг «атрибут джерела → поле довідника» (<c>ext.EntityFieldMap</c>, <c>ФВ-8.11</c>).</summary>
/// <param name="RegistryFieldDefId">Поле довідника.</param>
/// <param name="FieldCode">Код поля — для подій і плану.</param>
/// <param name="DataType">Тип поля: за ним значення приводиться до типу.</param>
/// <param name="UnitId">Одиниця поля (<c>RegistryFieldDef.UnitId</c>); лише для числових полів.</param>
/// <param name="SourceAttribute">Ім'я атрибута в джерелі.</param>
/// <param name="IsActive">Чи діє мапінг.</param>
/// <param name="RefRegistryDefId">
/// Для поля <c>Lookup</c>: довідник, на запис якого воно посилається
/// (<c>RegistryFieldDef.RefRegistryDefId</c>). Задано — атрибут джерела несе
/// КОД запису (<c>D-212</c> (5)), і планувальник розв'язує його в <c>Id</c> за
/// <see cref="RegistrySyncInput.LookupCodes"/>; <c>null</c> — значення вже є
/// <c>Id</c> (поведінка до <c>D-212</c>).
/// </param>
public sealed record RegistrySyncFieldMapping(
    int RegistryFieldDefId,
    string FieldCode,
    CellDataType DataType,
    int? UnitId,
    string SourceAttribute,
    bool IsActive,
    int? RefRegistryDefId = null);

/// <summary>
/// Код запису іншого довідника, який задача має розв'язати в <c>Id</c> ДО
/// планування (<see cref="RegistrySyncPlanner.LookupCodes"/>).
/// </summary>
/// <param name="RegistryDefId">Довідник, у якому шукати код.</param>
/// <param name="Code">Код запису (обрізаний від пробілів).</param>
public sealed record RegistrySyncLookupCode(int RegistryDefId, string Code);

/// <summary>Усе, що планувальнику треба знати про один прогін синхронізації одного довідника.</summary>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="SourceKind">Хто master для довідника (<c>ФВ-8.9</c>).</param>
/// <param name="IsCompleteSnapshot">
/// Знімок джерела прочитано ПОВНІСТЮ. Лише тоді відсутній елемент означає
/// «зник у джерелі» (<see cref="RegistrySyncEventKind.SourceMissing"/>); неповне
/// читання про зникнення нічого не каже (як <c>Missing</c> у <c>D-187</c>).
/// </param>
/// <param name="Elements">Елементи джерела.</param>
/// <param name="Links">Зв'язки <c>dic.RegistryExternalKey</c> цього довідника й цього джерела.</param>
/// <param name="Entries">Поточний стан прив'язаних записів.</param>
/// <param name="Mappings">Мапінги полів довідника.</param>
/// <param name="LookupCodes">
/// Розв'язані коди: довідник → (код → <c>Id</c> запису), для кодів із
/// <see cref="RegistrySyncPlanner.LookupCodes"/>. Коду немає в словнику (або
/// немає словника довідника) — такого запису немає: подія
/// <see cref="RegistrySyncEventKind.ValueRejected"/> з <c>err.ECR-REG-0422.entryRefNotFound</c>.
/// Порівняння кодів — компаратором словника (задача ставить той, що й у базі).
/// </param>
public sealed record RegistrySyncInput(
    int RegistryDefId,
    RegistrySourceKind SourceKind,
    bool IsCompleteSnapshot,
    IReadOnlyList<RegistrySyncSourceElement> Elements,
    IReadOnlyList<RegistrySyncLink> Links,
    IReadOnlyList<RegistrySyncEntryState> Entries,
    IReadOnlyList<RegistrySyncFieldMapping> Mappings,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>>? LookupCodes = null);

/// <summary>Одна зміна поля, яку синк має записати через <c>RegistryEntryWriter</c>.</summary>
/// <param name="RegistryEntryId">Запис довідника.</param>
/// <param name="RegistryFieldDefId">Поле.</param>
/// <param name="FieldCode">Код поля.</param>
/// <param name="OldValue">Поточне типізоване значення.</param>
/// <param name="NewValue">Нове типізоване значення з джерела.</param>
public sealed record RegistrySyncUpdate(
    long RegistryEntryId,
    int RegistryFieldDefId,
    string FieldCode,
    object? OldValue,
    object? NewValue);

/// <summary>Зміна шляху елемента в джерелі: <c>RegistryExternalKey.MarkSynced(path, …)</c>.</summary>
/// <param name="ExternalId">Елемент джерела.</param>
/// <param name="RegistryEntryId">Прив'язаний запис.</param>
/// <param name="OldPath">Збережений шлях.</param>
/// <param name="NewPath">Шлях у знімку.</param>
public sealed record RegistrySyncPathChange(
    string ExternalId,
    long RegistryEntryId,
    string? OldPath,
    string NewPath);

/// <summary>Вид події синхронізації.</summary>
public enum RegistrySyncEventKind
{
    /// <summary>
    /// Значення в джерелі інше, ніж у ECR, а писати синк не має права: довідник
    /// <c>Local</c> (лише звірка, <c>D-49</c>) або поле довідника <c>External</c>
    /// з вимкненим мапінгом. Нічого не пишеться.
    /// </summary>
    Diverged,

    /// <summary>Джерело змінило поле, яке останньою правила людина: лишається людське (<c>D-118</c>).</summary>
    ConflictKeptManual,

    /// <summary>Прив'язаного елемента немає в повному знімку джерела. Запис не видаляється.</summary>
    SourceMissing,

    /// <summary>Елемент джерела не має зв'язку з жодним записом. Запис не створюється автоматично.</summary>
    ElementUnlinked,

    /// <summary>Значення джерела не приводиться до типу поля.</summary>
    ValueRejected,

    /// <summary><c>External</c>: новий елемент джерела — запис створено синком (<c>D-212</c> (1)).</summary>
    AutoCreated,

    /// <summary>Елемента немає в повному знімку, політика <c>Deactivate</c> — запис вимкнено (<c>D-212</c>).</summary>
    Deactivated,

    /// <summary><c>External</c>: елемент повернувся після <see cref="Deactivated"/> — запис увімкнено (<c>D-212</c> Q6).</summary>
    Reactivated,

    /// <summary>Значення джерела порушує правило довідника; запис не змінено.</summary>
    RuleViolation,

    /// <summary>Зв'язок переприв'язано за запасним <c>ExternalPath</c> (<c>D-212</c> (7)).</summary>
    ExternalKeyRelinked,
}

/// <summary>Подія синхронізації — для журналу й панелі зовнішніх ідентифікаторів.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="ExternalId">Елемент джерела, якщо є.</param>
/// <param name="RegistryEntryId">Запис довідника, якщо є.</param>
/// <param name="FieldCode">Поле, якщо подія стосується поля.</param>
/// <param name="CurrentValue">Значення в ECR.</param>
/// <param name="SourceValue">Значення з джерела: типізоване, а для <see cref="RegistrySyncEventKind.ValueRejected"/> — сире.</param>
/// <param name="ErrorCode">Код відмови (<c>ECR-REG-0422</c>) для <see cref="RegistrySyncEventKind.ValueRejected"/>.</param>
/// <param name="MessageKey">Ключ каталогу відмови для <see cref="RegistrySyncEventKind.ValueRejected"/>.</param>
public sealed record RegistrySyncEvent(
    RegistrySyncEventKind Kind,
    string? ExternalId,
    long? RegistryEntryId,
    string? FieldCode = null,
    object? CurrentValue = null,
    object? SourceValue = null,
    string? ErrorCode = null,
    string? MessageKey = null);

/// <summary>План синхронізації: що записати і про що повідомити.</summary>
/// <param name="Updates">Зміни полів.</param>
/// <param name="PathChanges">Зміни шляхів у <c>dic.RegistryExternalKey</c>.</param>
/// <param name="Events">Події.</param>
public sealed record RegistrySyncPlan(
    IReadOnlyList<RegistrySyncUpdate> Updates,
    IReadOnlyList<RegistrySyncPathChange> PathChanges,
    IReadOnlyList<RegistrySyncEvent> Events)
{
    /// <summary>Нічого писати й нічого повідомляти — ідемпотентний прогін.</summary>
    public bool IsEmpty => Updates.Count == 0 && PathChanges.Count == 0 && Events.Count == 0;
}
