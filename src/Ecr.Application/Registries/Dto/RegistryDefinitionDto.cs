// src/Ecr.Application/Registries/Dto/RegistryDefinitionDto.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Dto;

/// <summary>
/// Повний опис довідника для конструктора (<c>ФВ-8.12</c>): поля, зв'язки,
/// правила, мапінг.
/// </summary>
/// <remarks>
/// ⛔ Чотири області приходять ОДНІЄЮ відповіддю, а не чотирма запитами. Вони
/// описують один об'єкт і читаються разом: поле <c>IsExternallyManaged</c> без
/// мапінгу поруч виглядає як звичайне, а правило <c>CrossRegistry</c> без
/// переліку зв'язків не має контексту. Чотири запити давали б чотири різні
/// моменти часу на одному екрані.
/// </remarks>
/// <param name="Id">Ідентифікатор довідника.</param>
/// <param name="Code">Код довідника.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="IsTemporal">Чи мають записи вікно чинності.</param>
/// <param name="SourceKind">Хто master (<c>ФВ-8.9</c>).</param>
/// <param name="DefinitionVersion">Версія опису; росте від зміни складу полів і правил.</param>
/// <param name="DataRevision">Ревізія даних; росте від зміни записів.</param>
/// <param name="Fields">Поля довідника в порядку показу.</param>
/// <param name="Relations">Зв'язки: посилання полів і види M:N, наявні в даних.</param>
/// <param name="Rules">Правила цілісності — чотири види (<c>H-10</c>).</param>
/// <param name="Mappings">Мапінг зовнішніх полів на поля довідника (<c>ФВ-8.11</c>).</param>
/// <param name="Keys">
/// Складені ключі довідника (<c>D-151</c>, RT-11), і вимкнені теж: вимкнений ключ пояснює, чому
/// колись діяла саме така унікальність.
/// </param>
/// <param name="CodeMode">Звідки береться код нового запису (<c>D-157</c>).</param>
/// <remarks>
/// ⚠ <paramref name="Keys"/> і <paramref name="CodeMode"/> мають типові значення лише заради
/// сумісності контракту (у схемі вони необов'язкові); сервер заповнює їх завжди.
/// </remarks>
public sealed record RegistryDefinitionDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    bool IsTemporal,
    Domain.Enums.RegistrySourceKind SourceKind,
    int DefinitionVersion,
    int DataRevision,
    IReadOnlyList<RegistryFieldDto> Fields,
    IReadOnlyList<RegistryRelationDto> Relations,
    IReadOnlyList<RegistryRuleDto> Rules,
    IReadOnlyList<RegistryMappingDto> Mappings,
    IReadOnlyList<RegistryKeyDto>? Keys = null,
    Domain.Enums.RegistryCodeMode? CodeMode = null);

/// <summary>Складений ключ довідника в описі (<c>D-151</c>, FEATURE-REGISTRY-TABLES §4.1).</summary>
/// <param name="Id">Ідентифікатор ключа.</param>
/// <param name="Code">Код ключа в межах довідника.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="FieldCodes">Поля ключа в порядку частин — це й порядок аргументів <c>REGFIND</c>.</param>
/// <param name="IsPrimary">Первинний ключ: ним шукає <c>REGFIND</c>.</param>
/// <param name="IgnoreCase">Текстові частини порівнюються без урахування регістру.</param>
/// <param name="IsActive">Чи діє ключ.</param>
public sealed record RegistryKeyDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    IReadOnlyList<string> FieldCodes,
    bool IsPrimary,
    bool IgnoreCase,
    bool IsActive);

/// <summary>Ключ, який зберігає конструктор (<c>D-151</c>).</summary>
/// <param name="Id"><c>null</c> — новий ключ; інакше — правка наявного.</param>
/// <param name="Code">Код ключа; у наявного не змінюється.</param>
/// <param name="NameL10n">Назва мовами каталогу; змінюється.</param>
/// <param name="FieldCodes">Коди полів у порядку частин (1–8); у наявного не змінюються.</param>
/// <param name="IsPrimary">Первинний ключ; у наявного не змінюється.</param>
/// <param name="IgnoreCase">Порівняння тексту без регістру; у наявного не змінюється.</param>
/// <param name="IsActive">Чи діє ключ; вимкнений — не перевіряється.</param>
/// <remarks>
/// ⛔ Склад, <paramref name="IsPrimary"/> і <paramref name="IgnoreCase"/> наявного ключа не
/// змінюються (<c>RegistryKeyDef</c>): будь-яка з цих змін мовчки перебудувала б хеш кожного
/// запису. Потрібен інший ключ — заводять новий, старий вимикають.
/// </remarks>
public sealed record RegistryKeySaveDto(
    int? Id,
    string Code,
    LocalizedText NameL10n,
    IReadOnlyList<string> FieldCodes,
    bool IsPrimary,
    bool IgnoreCase,
    bool IsActive);

/// <summary>Запит живої перевірки дублікатів ключа до збереження (§4.5).</summary>
/// <param name="FieldCodes">Коди полів майбутнього ключа в порядку частин.</param>
/// <param name="IgnoreCase">Порівнювати текст без урахування регістру (як у ключа).</param>
public sealed record RegistryKeyCheckRequest(
    IReadOnlyList<string> FieldCodes,
    bool IgnoreCase = true);

/// <summary>Результат перевірки дублікатів ключа на наявних даних (§4.5).</summary>
/// <param name="Checked">Скільки живих записів перевірено.</param>
/// <param name="Groups">Скільки значень ключа мають більше одного запису.</param>
/// <param name="Sample">Перші групи (не більше двадцяти) — приклади для людини.</param>
public sealed record RegistryKeyCheckResponse(
    int Checked,
    int Groups,
    IReadOnlyList<RegistryKeyDuplicateDto> Sample);

/// <summary>Одне значення ключа, яке мають кілька записів.</summary>
/// <param name="KeyText">Людський вигляд значення ключа.</param>
/// <param name="Entries">Записи з цим значенням.</param>
public sealed record RegistryKeyDuplicateDto(
    string KeyText,
    IReadOnlyList<RegistryKeyDuplicateEntryDto> Entries);

/// <summary>Запис у групі дублікатів.</summary>
/// <param name="Id">Ідентифікатор запису.</param>
/// <param name="Code">Код запису.</param>
public sealed record RegistryKeyDuplicateEntryDto(long Id, string Code);

/// <summary>
/// Зв'язок довідника з іншим довідником (<c>ФВ-8.4</c>).
/// </summary>
/// <remarks>
/// ⛔ Зв'язки ОБЧИСЛЮЮТЬСЯ, а не читаються з опису: таблиці
/// <c>cfg.RegistryRelationDef</c> у схемі немає (<c>Q-027</c>). Тому
/// <see cref="Kind"/> — це висновок із того, що є: поле, яке вказує на власний
/// довідник, дає ієрархію; на чужий — каскад; рядки
/// <c>dic.RegistryEntryLink</c> дають M:N.
/// </remarks>
/// <param name="Kind">
/// Вид: <c>Hierarchy</c>, <c>Cascade</c>, <c>Composition</c> (поле композиції, <c>D-155</c>) або
/// <c>Association</c>.
/// </param>
/// <param name="FieldCode">Поле-посилання; <c>null</c> для M:N — там поля немає.</param>
/// <param name="TargetRegistryDefId">Довідник-ціль; <c>null</c> для M:N.</param>
/// <param name="TargetRegistryCode">Код довідника-цілі; <c>null</c> для M:N.</param>
/// <param name="LinkKind">Вид відношення M:N; <c>null</c> для зв'язків через поле.</param>
/// <param name="LinkCount">Скільки зв'язків цього виду в даних; <c>null</c> для полів.</param>
/// <param name="OnParentDelete">
/// Що стається з частиною, коли видаляють батька; лише для <c>Composition</c>, інакше <c>null</c>.
/// </param>
public sealed record RegistryRelationDto(
    string Kind,
    string? FieldCode,
    int? TargetRegistryDefId,
    string? TargetRegistryCode,
    string? LinkKind,
    int? LinkCount,
    Domain.Enums.ParentDeletePolicy? OnParentDelete = null);

/// <summary>Правило цілісності довідника.</summary>
/// <param name="Id">Ідентифікатор правила; <c>0</c> — нове.</param>
/// <param name="Code">Код правила.</param>
/// <param name="RuleKind">Вид: один із чотирьох (<c>H-10</c>).</param>
/// <param name="Expression">Предикат діалектом виразів ECR.</param>
/// <param name="Severity">Рівень порушення.</param>
/// <param name="MessageL10n">Текст порушення мовами каталогу.</param>
/// <param name="ParametersJson">Параметри виду правила; <c>null</c> — немає.</param>
/// <param name="IsActive">Чи діє правило.</param>
public sealed record RegistryRuleDto(
    int Id,
    string Code,
    string RuleKind,
    string Expression,
    string Severity,
    LocalizedText MessageL10n,
    string? ParametersJson,
    bool IsActive);

/// <summary>Мапінг зовнішнього поля на поле довідника.</summary>
/// <param name="FieldMapId">Запис <c>ext.EntityFieldMap</c>.</param>
/// <param name="FieldCode">Поле довідника, куди лягає значення.</param>
/// <param name="SourceCode">Сутність джерела.</param>
/// <param name="SourceField">Поле або тег у джерелі.</param>
/// <param name="TransformCode">Згортання точок періоду; <c>null</c> — не згортається.</param>
/// <param name="SourceUnitCode">Одиниця джерела; <c>null</c> — безрозмірне.</param>
/// <param name="TargetUnitCode">Одиниця поля; <c>null</c> — безрозмірне.</param>
/// <param name="IsActive">Чи діє мапінг.</param>
public sealed record RegistryMappingDto(
    int FieldMapId,
    string FieldCode,
    string SourceCode,
    string SourceField,
    string? TransformCode,
    string? SourceUnitCode,
    string? TargetUnitCode,
    bool IsActive);

/// <summary>Запис історії довідника (<c>ФВ-8.12</c>).</summary>
/// <param name="ChangedAt">Момент зміни в UTC.</param>
/// <param name="EntityType">Що змінилося: опис довідника чи правило.</param>
/// <param name="Operation">Дія: <c>SaveDefinition</c>, <c>SwitchSourceSet</c>.</param>
/// <param name="OldJson">Стан до зміни.</param>
/// <param name="NewJson">Стан після зміни.</param>
/// <param name="ChangeReason">Причина зміни.</param>
/// <param name="ChangedByUserId">Автор.</param>
public sealed record RegistryHistoryEntryDto(
    DateTime ChangedAt,
    string EntityType,
    string Operation,
    string? OldJson,
    string? NewJson,
    string? ChangeReason,
    int ChangedByUserId);

/// <summary>Поле, яке зберігає конструктор.</summary>
/// <param name="Id"><c>null</c> — нове поле; інакше — правка наявного.</param>
/// <param name="Code">Код поля; у наявного не змінюється.</param>
/// <param name="NameL10n">Підпис мовами каталогу.</param>
/// <param name="DataType">Тип значення; у наявного не змінюється.</param>
/// <param name="Ordinal">Порядок у переліку.</param>
/// <param name="IsRequired">Обов'язковість.</param>
/// <param name="IsKey">Чи входить у бізнес-ключ; у наявного не змінюється.</param>
/// <param name="LookupRegistryDefId">Довідник-джерело; у наявного змінюється чи знімається (<c>null</c>), лише поки жодне значення поля не вказує на запис (ФВ-8.12).</param>
/// <param name="UnitId">Одиниця значення.</param>
/// <param name="RelationKind">
/// Посилання чи композиція (<c>D-155</c>) — лише для нового поля <c>Lookup</c>; <c>null</c> —
/// посилання (для наявного поля — без змін). У наявного не змінюється.
/// </param>
/// <param name="OnParentDelete">
/// Що робити з частиною при видаленні батька; <c>null</c> — <c>Restrict</c>. Читається лише для
/// композиції; у наявного не змінюється.
/// </param>
public sealed record RegistryFieldSaveDto(
    int? Id,
    string Code,
    LocalizedText NameL10n,
    string DataType,
    int Ordinal,
    bool IsRequired,
    bool IsKey,
    int? LookupRegistryDefId,
    int? UnitId,
    Domain.Enums.RegistryRelationKind? RelationKind = null,
    Domain.Enums.ParentDeletePolicy? OnParentDelete = null);

/// <summary>Правило, яке зберігає конструктор.</summary>
/// <param name="Id"><c>null</c> — нове правило; інакше — правка наявного.</param>
/// <param name="Code">Код правила; у наявного не змінюється.</param>
/// <param name="RuleKind">Вид правила; у наявного не змінюється.</param>
/// <param name="Expression">Предикат.</param>
/// <param name="Severity">Рівень порушення.</param>
/// <param name="MessageL10n">Текст порушення.</param>
/// <param name="ParametersJson">Параметри виду правила.</param>
/// <param name="IsActive">Чи діє правило.</param>
public sealed record RegistryRuleSaveDto(
    int? Id,
    string Code,
    string RuleKind,
    string Expression,
    string Severity,
    LocalizedText MessageL10n,
    string? ParametersJson,
    bool IsActive);

/// <summary>Запит на збереження опису довідника.</summary>
/// <param name="Fields">Повний перелік полів після правки.</param>
/// <param name="Rules">Повний перелік правил після правки.</param>
/// <param name="Reason">
/// Причина зміни. Обов'язкова: опис довідника змінює те, як читаються ВЖЕ
/// збережені записи, і питання «чому тут з'явилося це поле» ставлять через рік.
/// </param>
/// <param name="Keys">
/// Повний перелік ключів після правки (RT-11); ключ, якого в переліку немає, вимикається.
/// <c>null</c> — ключі не змінюються (клієнт, що про ключі не знає, їх не вимикає).
/// </param>
/// <param name="CodeMode">
/// Режим коду записів (<c>D-157</c>); <c>null</c> — без змін. Змінюється, лише поки в довіднику
/// немає жодного запису.
/// </param>
public sealed record SaveRegistryDefinitionDto(
    IReadOnlyList<RegistryFieldSaveDto> Fields,
    IReadOnlyList<RegistryRuleSaveDto> Rules,
    string Reason,
    IReadOnlyList<RegistryKeySaveDto>? Keys = null,
    Domain.Enums.RegistryCodeMode? CodeMode = null);

/// <summary>Запит на збереження чернетки опису (<c>BE-24</c> крок 2).</summary>
/// <param name="Fields">Повний перелік полів після правки.</param>
/// <param name="Rules">Повний перелік правил після правки.</param>
/// <param name="Reason">Причина зміни; при публікації йде в журнал.</param>
/// <param name="RowVersion">
/// Версія чернетки, від якої відштовхується правка; <c>null</c> — чернетки ще немає.
/// </param>
/// <param name="Keys">Повний перелік ключів; <c>null</c> — публікація ключів не змінює.</param>
/// <param name="CodeMode">Режим коду записів; <c>null</c> — без змін.</param>
public sealed record SaveRegistryDefinitionDraftRequest(
    IReadOnlyList<RegistryFieldSaveDto> Fields,
    IReadOnlyList<RegistryRuleSaveDto> Rules,
    string Reason,
    string? RowVersion,
    IReadOnlyList<RegistryKeySaveDto>? Keys = null,
    Domain.Enums.RegistryCodeMode? CodeMode = null);

/// <summary>Запит на публікацію чернетки опису.</summary>
/// <param name="RowVersion">Версія чернетки, яку публікують.</param>
public sealed record PublishRegistryDefinitionRequest(string RowVersion);

/// <summary>Чернетка опису довідника.</summary>
/// <param name="BaseDefinitionVersion">Версія опублікованого опису, від якої відштовхується чернетка.</param>
/// <param name="Fields">Поля чернетки.</param>
/// <param name="Rules">Правила чернетки.</param>
/// <param name="Reason">Причина зміни.</param>
/// <param name="UpdatedAt">Момент останнього збереження, UTC.</param>
/// <param name="UpdatedByUserId">Хто зберіг востаннє.</param>
/// <param name="RowVersion">Версія для наступного збереження чи публікації.</param>
/// <param name="Keys">Ключі чернетки; <c>null</c> — чернетка ключів не змінює.</param>
/// <param name="CodeMode">Режим коду чернетки; <c>null</c> — без змін.</param>
public sealed record RegistryDefinitionDraftDto(
    int BaseDefinitionVersion,
    IReadOnlyList<RegistryFieldSaveDto> Fields,
    IReadOnlyList<RegistryRuleSaveDto> Rules,
    string Reason,
    DateTime UpdatedAt,
    int UpdatedByUserId,
    string RowVersion,
    IReadOnlyList<RegistryKeySaveDto>? Keys = null,
    Domain.Enums.RegistryCodeMode? CodeMode = null);

/// <summary>Стан чернетки опису довідника.</summary>
/// <param name="DefinitionVersion">Поточна версія ОПУБЛІКОВАНОГО опису.</param>
/// <param name="Draft">Чернетка; <c>null</c> — чернетки немає.</param>
public sealed record RegistryDefinitionDraftStateResponse(
    int DefinitionVersion,
    RegistryDefinitionDraftDto? Draft);
