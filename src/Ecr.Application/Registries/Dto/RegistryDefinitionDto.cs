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
    IReadOnlyList<RegistryMappingDto> Mappings);

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
/// <param name="Kind">Вид: <c>Hierarchy</c>, <c>Cascade</c> або <c>Association</c>.</param>
/// <param name="FieldCode">Поле-посилання; <c>null</c> для M:N — там поля немає.</param>
/// <param name="TargetRegistryDefId">Довідник-ціль; <c>null</c> для M:N.</param>
/// <param name="TargetRegistryCode">Код довідника-цілі; <c>null</c> для M:N.</param>
/// <param name="LinkKind">Вид відношення M:N; <c>null</c> для зв'язків через поле.</param>
/// <param name="LinkCount">Скільки зв'язків цього виду в даних; <c>null</c> для полів.</param>
public sealed record RegistryRelationDto(
    string Kind,
    string? FieldCode,
    int? TargetRegistryDefId,
    string? TargetRegistryCode,
    string? LinkKind,
    int? LinkCount);

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
/// <param name="LookupRegistryDefId">Довідник-джерело; у наявного не змінюється.</param>
/// <param name="UnitId">Одиниця значення.</param>
public sealed record RegistryFieldSaveDto(
    int? Id,
    string Code,
    LocalizedText NameL10n,
    string DataType,
    int Ordinal,
    bool IsRequired,
    bool IsKey,
    int? LookupRegistryDefId,
    int? UnitId);

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
public sealed record SaveRegistryDefinitionDto(
    IReadOnlyList<RegistryFieldSaveDto> Fields,
    IReadOnlyList<RegistryRuleSaveDto> Rules,
    string Reason);
