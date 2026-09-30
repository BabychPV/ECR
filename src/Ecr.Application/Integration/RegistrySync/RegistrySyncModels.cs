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
/// <param name="Name">
/// Ім'я елемента в джерелі: назва (і в <c>CodeMode = Manual</c> — код) автоствореного
/// запису (<c>D-212</c> Q4). <c>null</c> — джерело не дало імені, автостворення немає.
/// </param>
public sealed record RegistrySyncSourceElement(
    string ExternalId,
    string? ExternalPath,
    IReadOnlyDictionary<string, object?> Attributes,
    string? Name = null);

/// <summary>
/// Наявний зв'язок «елемент джерела → запис довідника»: рядок
/// <c>dic.RegistryExternalKey</c> (<c>ФВ-8.10</c>).
/// </summary>
/// <param name="ExternalId">Ідентифікатор елемента в джерелі.</param>
/// <param name="RegistryEntryId">Запис довідника.</param>
/// <param name="ExternalPath">Шлях, збережений під час останньої синхронізації; запасний ключ перепривʼязки (<c>D-212</c> (7)).</param>
/// <param name="MissingInSourceSince">
/// Відколи елемента немає в джерелі (<c>RegistryExternalKey.MissingInSourceSince</c>);
/// <c>null</c> — є або не перевірялося.
/// </param>
public sealed record RegistrySyncLink(
    string ExternalId,
    long RegistryEntryId,
    string? ExternalPath,
    DateTime? MissingInSourceSince = null);

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
/// <param name="IsActive">Запис увімкнено (<c>RegistryEntry.IsActive</c>); вимкнений — кандидат на повернення (<c>D-212</c> Q6).</param>
/// <param name="ValidFrom">Поточний початок вікна дії (<c>RegistryEntry.ValidFrom</c>).</param>
/// <param name="ValidTo">Поточний виключний кінець вікна дії (<c>RegistryEntry.ValidTo</c>).</param>
/// <param name="WindowLastWriterIsHuman">
/// Рядок запису останньою змінювала людина (<c>RegistryEntry.ChangedByUserId</c> — не
/// <c>svc-integration</c> або невідомий): у <c>Hybrid</c> вікно тоді не перезаписується
/// (<c>D-118</c>, <c>D-212</c> (2)). Для <c>External</c> не діє.
/// </param>
public sealed record RegistrySyncEntryState(
    long RegistryEntryId,
    IReadOnlyDictionary<int, RegistrySyncCurrentValue> Values,
    bool IsActive = true,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool WindowLastWriterIsHuman = false);

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
/// <param name="CodeMode">
/// Звідки код автоствореного запису (<c>D-212</c> Q4): <c>Auto</c> — видасть writer
/// із послідовності, <c>Manual</c> — ім'я елемента.
/// </param>
/// <param name="OnMissingInSource">
/// Що робити з записом, чий елемент зник із ПОВНОГО знімка
/// (<c>ext.SourceEntity.OnMissingInSource</c>, <c>D-212</c> (3), Q5).
/// </param>
/// <param name="Validity">
/// Вікно дії з атрибутів AF (<c>D-212</c> (8), PR-7); <c>null</c> — вікно не синхронізується
/// (мапінг дат вимкнений — дефолт, або довідник не темпоральний).
/// </param>
public sealed record RegistrySyncInput(
    int RegistryDefId,
    RegistrySourceKind SourceKind,
    bool IsCompleteSnapshot,
    IReadOnlyList<RegistrySyncSourceElement> Elements,
    IReadOnlyList<RegistrySyncLink> Links,
    IReadOnlyList<RegistrySyncEntryState> Entries,
    IReadOnlyList<RegistrySyncFieldMapping> Mappings,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>>? LookupCodes = null,
    RegistryCodeMode CodeMode = RegistryCodeMode.Manual,
    RegistryMissingPolicy OnMissingInSource = RegistryMissingPolicy.MarkOrphaned,
    RegistrySyncValiditySource? Validity = null);

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

/// <summary>Значення поля нового запису.</summary>
/// <param name="RegistryFieldDefId">Поле.</param>
/// <param name="FieldCode">Код поля (адресація writer'а).</param>
/// <param name="Value">Типізоване значення з джерела (не <c>null</c>).</param>
public sealed record RegistrySyncFieldValue(int RegistryFieldDefId, string FieldCode, object Value);

/// <summary>
/// <c>External</c>: новий елемент повного знімка — створити запис і зв'язок
/// <c>dic.RegistryExternalKey</c> (<c>D-212</c> (1), Q4). Виконує задача (PR-6) через
/// writer у режимі «лише створювати»; подію <see cref="RegistrySyncEventKind.AutoCreated"/>
/// пише вона ж — після успіху, з <c>Id</c> нового запису.
/// </summary>
/// <param name="ExternalId">Елемент джерела.</param>
/// <param name="ExternalPath">Шлях елемента — у зв'язок.</param>
/// <param name="Code">Код запису; <c>null</c> — <c>CodeMode = Auto</c>, код видасть writer із послідовності.</param>
/// <param name="DisplayName">Назва запису — ім'я елемента.</param>
/// <param name="Values">Значення змаплених полів, що привелися до типу (відмови — події, поля тут немає).</param>
public sealed record RegistrySyncCreate(
    string ExternalId,
    string? ExternalPath,
    string? Code,
    string DisplayName,
    IReadOnlyList<RegistrySyncFieldValue> Values)
{
    /// <summary>Вікно дії з атрибутів AF (<c>D-212</c> PR-7); <c>null</c> — без обмеження.</summary>
    public Ecr.Domain.ValueObjects.ValidityWindow? Validity { get; init; }
}

/// <summary>
/// Зв'язок <c>dic.RegistryExternalKey</c>, на якому треба поставити
/// (<c>MarkMissing</c>) або зняти (<c>ClearMissing</c>) позначку зникнення.
/// </summary>
/// <param name="ExternalId">Елемент джерела.</param>
/// <param name="RegistryEntryId">Прив'язаний запис.</param>
public sealed record RegistrySyncLinkMark(string ExternalId, long RegistryEntryId);

/// <summary>
/// Вимкнути (<c>RegistryEntry.Deactivate</c>) або ввімкнути (<c>Activate</c>) запис.
/// Подію <see cref="RegistrySyncEventKind.Deactivated"/>/<see cref="RegistrySyncEventKind.Reactivated"/>
/// пише виконавець (PR-6) після успіху.
/// </summary>
/// <param name="RegistryEntryId">Запис.</param>
/// <param name="ExternalId">Елемент джерела — для події.</param>
public sealed record RegistrySyncActivation(long RegistryEntryId, string ExternalId);

/// <summary>
/// Перепривʼязка за запасним ключем <c>ExternalPath</c> (<c>D-212</c> (7)): елемент
/// перестворено в AF з новим GUID — <c>RegistryExternalKey.Relink(new)</c> +
/// <c>MarkSynced(path)</c>. Подію <see cref="RegistrySyncEventKind.ExternalKeyRelinked"/>
/// пише виконавець (PR-6) після успіху.
/// </summary>
/// <param name="RegistryEntryId">Запис.</param>
/// <param name="OldExternalId">Зниклий ідентифікатор (поточний у зв'язку).</param>
/// <param name="NewExternalId">Ідентифікатор елемента зі знімка.</param>
/// <param name="Path">Спільний шлях (зі знімка).</param>
public sealed record RegistrySyncRelink(long RegistryEntryId, string OldExternalId, string NewExternalId, string Path);

/// <summary>Вид події синхронізації.</summary>
public enum RegistrySyncEventKind
{
    /// <summary>
    /// Значення в джерелі інше, ніж у ECR, а писати синк не має права: довідник
    /// <c>Local</c> (лише звірка, <c>D-49</c>) або поле довідника <c>External</c>
    /// з вимкненим мапінгом. Нічого не пишеться.
    /// ⚠ Також <c>Hybrid</c>: елемент повернувся, а запис вимкнено синком — поле
    /// <see cref="RegistrySyncPlanner.ActiveFieldCode"/>, ECR <c>false</c>, джерело
    /// <c>true</c>; вмикає людина (<c>D-212</c> Q6).
    /// </summary>
    Diverged,

    /// <summary>Джерело змінило поле, яке останньою правила людина: лишається людське (<c>D-118</c>).</summary>
    ConflictKeptManual,

    /// <summary>
    /// Прив'язаного елемента немає в повному знімку джерела. Запис не видаляється.
    /// Політика <c>MarkOrphaned</c> (плюс позначка зв'язку), <c>Ignore</c> (запис не
    /// чіпається, Q5), <c>Local</c> і неоднозначна перепривʼязка.
    /// </summary>
    SourceMissing,

    /// <summary>
    /// Елемент джерела не має зв'язку з жодним записом, і запис не створюється:
    /// <c>Hybrid</c>/<c>Local</c> (лише сповіщення, <c>D-212</c> (2)), неповний знімок
    /// або елемент без імені.
    /// </summary>
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
    /// <summary>Автостворення записів (<c>External</c>, повний знімок).</summary>
    public IReadOnlyList<RegistrySyncCreate> Creates { get; init; } = [];

    /// <summary>Перепривʼязки за <c>ExternalPath</c> (повний знімок, рівно один кандидат).</summary>
    public IReadOnlyList<RegistrySyncRelink> Relinks { get; init; } = [];

    /// <summary>Позначити зв'язок зниклим (<c>MarkOrphaned</c>, <c>Deactivate</c>); лише ще не позначені.</summary>
    public IReadOnlyList<RegistrySyncLinkMark> MissingMarks { get; init; } = [];

    /// <summary>Зняти позначку зникнення: елемент знову є в знімку.</summary>
    public IReadOnlyList<RegistrySyncLinkMark> MissingClears { get; init; } = [];

    /// <summary>Вимкнути запис (<c>Deactivate</c>); лише ще ввімкнені.</summary>
    public IReadOnlyList<RegistrySyncActivation> Deactivations { get; init; } = [];

    /// <summary><c>External</c>: увімкнути запис, чий елемент повернувся (<c>D-212</c> Q6).</summary>
    public IReadOnlyList<RegistrySyncActivation> Reactivations { get; init; } = [];

    /// <summary>Змінити вікно дії записів (<c>D-212</c> PR-7; <c>External</c>/<c>Hybrid</c>).</summary>
    public IReadOnlyList<RegistrySyncValidityChange> ValidityChanges { get; init; } = [];

    /// <summary>Нічого писати й нічого повідомляти — ідемпотентний прогін.</summary>
    public bool IsEmpty => Updates.Count == 0 && PathChanges.Count == 0 && Events.Count == 0
                           && Creates.Count == 0 && Relinks.Count == 0 && MissingMarks.Count == 0
                           && MissingClears.Count == 0 && Deactivations.Count == 0 && Reactivations.Count == 0
                           && ValidityChanges.Count == 0;
}
