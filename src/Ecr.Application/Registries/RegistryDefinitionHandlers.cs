// src/Ecr.Application/Registries/RegistryDefinitionHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>
/// Опис довідника для конструктора (<c>ФВ-8.12</c>): поля, зв'язки, правила,
/// мапінг — однією відповіддю.
/// </summary>
/// <remarks>
/// ⛔ Це НЕ те саме, що <see cref="ListRegistriesHandler"/>. Той віддає перелік
/// довідників для вибору — метадані десятків довідників; цей віддає ОДИН
/// довідник у повноті, разом із тим, що в переліку не потрібне нікому: правила,
/// мапінг і види зв'язків, які треба питати в трьох різних таблиць. Класти це
/// в перелік означало б робити три зайві запити на кожне відкриття сторінки
/// довідників.
/// </remarks>
public sealed class GetRegistryDefinitionHandler(
    IRegistryStore registries, Security.IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає повний опис довідника.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    public async Task<RegistryDefinitionDto> HandleAsync(string code, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Довідника «{code}» не існує.");

        var all = await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
        var byId = all.ToDictionary(d => d.Id, d => d.Code);

        var rules = await registries.ListRulesAsync(definition.Id, ct).ConfigureAwait(false);
        var mappings = await registries.ListFieldMappingsAsync(definition.Id, ct).ConfigureAwait(false);
        var linkKinds = await registries.ListLinkKindsAsync(definition.Id, ct).ConfigureAwait(false);

        var fields = definition.Fields.OrderBy(f => f.Ordinal).ToList();
        var fieldCodeById = fields.ToDictionary(f => f.Id, f => f.Code);

        return new RegistryDefinitionDto(
            definition.Id,
            definition.Code,
            definition.NameL10n,
            definition.IsTemporal,
            definition.SourceKind,
            definition.DefinitionVersion,
            definition.DataRevision,
            fields
                .Select(f => new RegistryFieldDto(
                    f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.IsRequired,
                    IsScopeField: f.IsKey, f.RefRegistryDefId, f.UnitId))
                .ToList(),
            Relations(definition, fields, byId, linkKinds),
            rules
                .Select(r => new RegistryRuleDto(
                    r.Id, r.Code, r.RuleKind.ToString(), r.Expression, r.Severity.ToString(),
                    r.MessageL10n, r.ParametersJson, r.IsActive))
                .ToList(),
            mappings

                // ⚠ Мапінг, що вказує на поле, якого в довіднику вже немає,
                // сюди не потрапляє — і це правильно: показати його ніде, а
                // мовчазний рядок «поле ?» на екрані гірший за його відсутність.
                .Where(m => fieldCodeById.ContainsKey(m.RegistryFieldDefId))
                .Select(m => new RegistryMappingDto(
                    m.FieldMapId, fieldCodeById[m.RegistryFieldDefId], m.SourceCode, m.SourceField,
                    m.TransformCode, m.SourceUnitCode, m.TargetUnitCode, m.IsActive))
                .ToList());
    }

    /// <summary>Зводить зв'язки з полів і з рядків <c>dic.RegistryEntryLink</c>.</summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="fields">Його поля.</param>
    /// <param name="codesById">Коди всіх довідників — для назви цілі.</param>
    /// <param name="linkKinds">Види зв'язків M:N, наявні в даних.</param>
    private static List<RegistryRelationDto> Relations(
        RegistryDef definition,
        IReadOnlyList<RegistryFieldDef> fields,
        Dictionary<int, string> codesById,
        IReadOnlyList<RegistryLinkKindStat> linkKinds)
    {
        var relations = new List<RegistryRelationDto>();

        foreach (var field in fields.Where(f => f.RefRegistryDefId is not null))
        {
            var target = field.RefRegistryDefId!.Value;

            relations.Add(new RegistryRelationDto(

                // ⚠ Поле, що вказує на ВЛАСНИЙ довідник, — це ієрархія
                // (`WasteGroup → WasteItem`), а не каскад: список звужує
                // батьківський запис того самого довідника.
                Kind: target == definition.Id ? "Hierarchy" : "Cascade",
                FieldCode: field.Code,
                TargetRegistryDefId: target,
                TargetRegistryCode: codesById.TryGetValue(target, out var targetCode) ? targetCode : null,
                LinkKind: null,
                LinkCount: null));
        }

        relations.AddRange(linkKinds.Select(k => new RegistryRelationDto(
            Kind: "Association",
            FieldCode: null,
            TargetRegistryDefId: null,
            TargetRegistryCode: null,
            LinkKind: k.LinkKind,
            LinkCount: k.Count)));

        return relations;
    }
}

/// <summary>
/// Історія довідника (<c>ФВ-8.12</c>): хто і коли міняв його опис.
/// </summary>
/// <remarks>
/// ⛔ Історія читається з <c>aud.StructureChange</c>, а не будується з версій:
/// версія опису каже, що змін було сім, і не каже про жодну з них. Питання, на
/// яке цей екран відповідає, — «чому тут з'явилося це поле», і відповідь на
/// нього — причина зміни, записана автором.
/// </remarks>
public sealed class GetRegistryHistoryHandler(
    IRegistryStore registries,
    IAuditReader audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Скільки останніх записів історії віддавати.</summary>
    /// <remarks>
    /// Структурних змін довідника одиниці на рік; сотня покриває весь час
    /// життя системи і не дає запиту вирости, якщо це припущення не справдиться.
    /// </remarks>
    public const int Limit = 100;

    /// <summary>Типи сутностей, з яких складається історія довідника.</summary>
    private static readonly string[] Types = ["cfg.RegistryDef", "cfg.RegistryRuleDef"];

    /// <summary>Читає історію змін довідника.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    public async Task<IReadOnlyList<RegistryHistoryEntryDto>> HandleAsync(
        string code, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Довідника «{code}» не існує.");

        var own = await audit
            .ReadStructureChangesAsync(Types, definition.Id, Limit, ct)
            .ConfigureAwait(false);

        // ⛔ Перемикання master пишеться ОДНИМ записом на весь набір із
        // `EntityId = 0` (`ФВ-13.10`): сутності «група довідників» немає, і
        // зв'язок між перемкнутими разом довідниками живе саме в цьому записі.
        // Тому історія одного довідника без нього неповна — а показати
        // ЧУЖИЙ набір було б гірше за пропуск. Фільтр іде по РОЗІБРАНОМУ
        // JSON, а не по підрядку: код `WATER` міститься в `WATER_BODY`, і
        // пошук підрядком приписав би довіднику чуже перемикання.
        var sets = await audit
            .ReadStructureChangesAsync(["cfg.RegistryDef"], 0, Limit, ct)
            .ConfigureAwait(false);

        return own
            .Concat(sets.Where(s => Mentions(s.NewJson, definition.Code)))
            .OrderByDescending(c => c.ChangedAt)
            .Take(Limit)
            .Select(c => new RegistryHistoryEntryDto(
                c.ChangedAt, c.EntityType, c.Operation, c.OldJson, c.NewJson,
                c.ChangeReason, c.ChangedByUserId))
            .ToList();
    }

    /// <summary>Чи називає запис аудиту саме цей довідник у наборі.</summary>
    /// <param name="newJson">Тіло <c>NewJson</c> запису аудиту.</param>
    /// <param name="code">Код довідника.</param>
    private static bool Mentions(string? newJson, string code)
    {
        if (string.IsNullOrWhiteSpace(newJson))
        {
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(newJson);
            if (!parsed.RootElement.TryGetProperty("registryCodes", out var codes)
                || codes.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return codes.EnumerateArray().Any(
                c => c.ValueKind == JsonValueKind.String
                     && string.Equals(c.GetString(), code, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            // ⚠ Журнал append-only і може містити записи старіших форматів.
            // Нерозбірливий запис не є приводом впасти на читанні історії.
            return false;
        }
    }
}

/// <summary>
/// Збереження опису довідника: полів і правил (<c>ФВ-8.12</c>, <c>H-10</c>).
/// </summary>
/// <remarks>
/// ⛔ Поля і правила зберігаються ОДНІЄЮ транзакцією. Правило
/// <c>RequiredWhen</c> посилається на поле, і збереження правил окремо від
/// полів дало б стан, у якому правило вказує на поле, якого ще немає, — тобто
/// довідник, який не проходить власну перевірку.
///
/// ⛔ Право <c>Registry.EditDefinition</c>, а не <c>Registry.EditData</c>: це
/// різні люди. Той, хто заводить речовину, і той, хто вирішує, що в довіднику
/// речовин узагалі є поле «клас небезпеки», — не одна роль.
/// </remarks>
public sealed class SaveRegistryDefinitionHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну ОПИСУ довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Зберігає поля і правила довідника.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="dto">Повний стан опису після правки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Нова версія опису.</returns>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    /// <exception cref="BusinessRuleException">Опис суперечливий — <c>ECR-REG-0422</c>.</exception>
    public async Task<int> HandleAsync(
        string code, SaveRegistryDefinitionDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не змінює довідники.");

        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Причина зміни опису обов'язкова: опис змінює те, як читаються вже збережені записи.");
        }

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Довідника «{code}» не існує.");

        var rules = await registries.ListRulesAsync(definition.Id, ct).ConfigureAwait(false);

        var before = Snapshot(definition, rules);

        ApplyFields(definition, dto.Fields);
        var applied = ApplyRules(definition, rules, dto.Rules);

        // ⛔ Обов'язковість перевіряється ПІСЛЯ застосування, на цілому описі:
        // довідник без жодного ключового поля не має бізнес-ключа, і його
        // записи неможливо зіставити ні з зовнішнім джерелом, ні між версіями.
        if (!definition.Fields.Any(f => f.IsKey))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Довідник «{definition.Code}» лишився б без жодного ключового поля: "
                + "бізнес-ключ запису не було б із чого скласти.");
        }

        definition.BumpDefinitionVersion();

        // ⚠ Знімок «після» береться з ПАМ'ЯТІ, а не повторним читанням: щойно
        // додані правила ще не збережені, і запит до бази віддав би стан «до»
        // під виглядом стану «після» — тобто журнал, який мовчки бреше саме
        // там, де змін найбільше.
        var after = Snapshot(definition, applied);

        // ⛔ Q-244: аудит і `SaveChanges` тепер одна транзакція — до цієї
        // правки аудит писався сирим SQL без жодної відкритої транзакції, і
        // збій між ним і `SaveChangesAsync` лишав журнал і опис довідника
        // розсинхронізованими.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,

                    // Довідник не належить версії шаблону: він один на всі проєкти
                    // і всі версії.
                    TemplateVersionId: 0,
                    EntityType: "cfg.RegistryDef",
                    EntityId: definition.Id,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "SaveDefinition",
                    OldJson: before,
                    NewJson: after,
                    ChangeReason: dto.Reason,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return definition.DefinitionVersion;
    }

    /// <summary>Застосовує перелік полів до опису.</summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="wanted">Поля після правки.</param>
    private static void ApplyFields(RegistryDef definition, IReadOnlyList<RegistryFieldSaveDto> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        var existing = definition.Fields.ToDictionary(f => f.Id);

        // ⛔ Поле НЕ ВИДАЛЯЄТЬСЯ (`D2-203`). `dic.RegistryValue` посилається на
        // нього зовнішнім ключем, і видалення поля означало б видалення значень
        // — тобто те саме тихе зникнення історії, від якого захищає `ФВ-8.6`.
        // Поле, що більше не потрібне, лишається необов'язковим і порожнім.
        var dropped = existing.Keys
            .Except(wanted.Where(f => f.Id is not null).Select(f => f.Id!.Value))
            .ToList();

        if (dropped.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Поле довідника не видаляється: на його значення посилаються записи. "
                + "Зробіть його необов'язковим — історія лишиться читабельною. "
                + $"Полів у запиті бракує: {dropped.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        foreach (var field in wanted)
        {
            if (field.Id is { } id)
            {
                if (!existing.TryGetValue(id, out var target))
                {
                    throw new NotFoundException(
                        "ECR-REG-0404",
                        $"Поля {id.ToString(CultureInfo.InvariantCulture)} у довіднику «{definition.Code}» немає.");
                }

                // ⛔ Код і тип наявного поля не змінюються — див. XML-doc
                // `RegistryFieldDef.Update`. Розбіжність тут означає, що клієнт
                // надіслав чуже поле під наявним Id, і мовчки застосувати з
                // цього лише підпис було б гірше за відмову.
                if (!string.Equals(target.Code, field.Code, StringComparison.Ordinal))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Код поля «{target.Code}» не змінюється: на нього посилаються вирази і мапінг.");
                }

                if (!string.Equals(target.DataType.ToString(), field.DataType, StringComparison.Ordinal))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Тип поля «{target.Code}» не змінюється: він визначає, як читаються вже збережені значення.");
                }

                target.Update(field.NameL10n, field.Ordinal, field.IsRequired);
                continue;
            }

            if (!Enum.TryParse<CellDataType>(field.DataType, ignoreCase: false, out var dataType))
            {
                throw new BusinessRuleException(
                    "ECR-REG-0422", $"Тип поля «{field.DataType}» не існує.");
            }

            var created = new RegistryFieldDef(
                definition.Id, EcrCode.Create(field.Code), field.NameL10n, dataType, field.Ordinal);

            created.Update(field.NameL10n, field.Ordinal, field.IsRequired);
            created.MarkKey(field.IsKey);
            created.PointTo(field.LookupRegistryDefId);
            created.MeasureIn(field.UnitId);

            // ⚠ Нове поле обов'язковим бути НЕ може: наявні записи його не
            // мають, і вимога значення зробила б увесь довідник недійсним
            // одразу після збереження. Заповнити його треба спершу даними.
            if (field.IsRequired)
            {
                throw new BusinessRuleException(
                    "ECR-REG-0422",
                    $"Нове поле «{field.Code}» не може бути обов'язковим: наявні записи його не мають. "
                    + "Заведіть його необов'язковим, заповніть і лише тоді вимагайте.");
            }

            definition.AddField(created);
        }
    }

    /// <summary>Застосовує перелік правил до опису.</summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="existing">Наявні правила.</param>
    /// <param name="wanted">Правила після правки.</param>
    /// <returns>Повний перелік правил після застосування — разом із новими.</returns>
    private List<RegistryRuleDef> ApplyRules(
        RegistryDef definition,
        IReadOnlyList<RegistryRuleDef> existing,
        IReadOnlyList<RegistryRuleSaveDto> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        var byId = existing.ToDictionary(r => r.Id);
        var result = new List<RegistryRuleDef>(existing);

        // ⛔ Правило, якого немає в запиті, ВИМИКАЄТЬСЯ, а не зникає:
        // правило, що колись діяло, — єдине пояснення того, чому наявні записи
        // виглядають саме так. Видалене правило робить це пояснення недоступним
        // назавжди.
        foreach (var id in byId.Keys.Except(wanted.Where(r => r.Id is not null).Select(r => r.Id!.Value)))
        {
            byId[id].SetActive(false);
        }

        foreach (var rule in wanted)
        {
            if (!Enum.TryParse<ValidationSeverity>(rule.Severity, ignoreCase: false, out var severity))
            {
                throw new BusinessRuleException(
                    "ECR-REG-0422", $"Рівень «{rule.Severity}» не існує.");
            }

            if (rule.Id is { } id)
            {
                if (!byId.TryGetValue(id, out var target))
                {
                    throw new NotFoundException(
                        "ECR-REG-0404",
                        $"Правила {id.ToString(CultureInfo.InvariantCulture)} у довіднику «{definition.Code}» немає.");
                }

                // ⛔ Вид правила не змінюється: параметри і предикат означають
                // для кожного виду різне, і зміна виду при збережених
                // параметрах дала б правило, яке перевіряє не те.
                if (!string.Equals(target.RuleKind.ToString(), rule.RuleKind, StringComparison.Ordinal))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Вид правила «{target.Code}» не змінюється: заведіть нове правило потрібного виду.");
                }

                target.Update(rule.Expression, severity, rule.MessageL10n, rule.ParametersJson);
                target.SetActive(rule.IsActive);
                continue;
            }

            if (!Enum.TryParse<RegistryRuleKind>(rule.RuleKind, ignoreCase: false, out var kind))
            {
                // ⛔ Перелік закритий чотирма видами (`H-10`). Невідомий вид не
                // «ігнорується»: правило, якого рушій не знає, не спрацьовує
                // ніколи і виглядає при цьому налаштованим.
                throw new BusinessRuleException(
                    "ECR-REG-0422",
                    $"Виду правила «{rule.RuleKind}» не існує: видів довідникових правил рівно чотири — "
                    + "RequiredWhen, UniqueWithin, Expression, CrossRegistry.");
            }

            var created = new RegistryRuleDef(
                definition.Id, EcrCode.Create(rule.Code), kind, rule.Expression, severity,
                rule.MessageL10n, rule.ParametersJson);

            created.SetActive(rule.IsActive);
            registries.AddRule(created);
            result.Add(created);
        }

        return result;
    }

    /// <summary>Знімок опису для журналу: саме він відповідає «що змінилося».</summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="rules">Його правила.</param>
    private static string Snapshot(RegistryDef definition, IReadOnlyList<RegistryRuleDef> rules)
        => JsonSerializer.Serialize(new
        {
            definitionVersion = definition.DefinitionVersion,
            fields = definition.Fields
                .OrderBy(f => f.Ordinal)
                .Select(f => new
                {
                    f.Code,
                    dataType = f.DataType.ToString(),
                    f.Ordinal,
                    f.IsRequired,
                    f.IsKey,
                    f.RefRegistryDefId,
                    f.UnitId,
                })
                .ToList(),
            rules = rules
                .OrderBy(r => r.Code, StringComparer.Ordinal)
                .Select(r => new
                {
                    r.Code,
                    ruleKind = r.RuleKind.ToString(),
                    r.Expression,
                    severity = r.Severity.ToString(),
                    r.ParametersJson,
                    r.IsActive,
                })
                .ToList(),
        });
}




