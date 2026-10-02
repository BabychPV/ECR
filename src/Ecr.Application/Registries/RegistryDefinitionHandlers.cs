// src/Ecr.Application/Registries/RegistryDefinitionHandlers.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    IRegistryStore registries,
    IRegistryKeyStore keys,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає повний опис довідника.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    public async Task<RegistryDefinitionDto> HandleAsync(string code, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{code}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = code });

        // ⛔ S18: заборона на довідник перекриває глобальне Registry.View — 404, як неіснуючий.
        RegistryAccess.EnsureNotDenied(profile, definition.Id, code);

        var all =await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
        var byId = all.ToDictionary(d => d.Id, d => d.Code);

        var rules = await registries.ListRulesAsync(definition.Id, ct).ConfigureAwait(false);
        var mappings = await registries.ListFieldMappingsAsync(definition.Id, ct).ConfigureAwait(false);
        var linkKinds = await registries.ListLinkKindsAsync(definition.Id, ct).ConfigureAwait(false);

        var fields = definition.Fields.OrderBy(f => f.Ordinal).ToList();
        var fieldCodeById = fields.ToDictionary(f => f.Id, f => f.Code);
        var keyDefs = await keys.ListKeysForUpdateAsync(definition.Id, ct).ConfigureAwait(false);

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
                .ToList(),
            [.. keyDefs.Select(k => new RegistryKeyDto(
                k.Id, k.Code, k.NameL10n, KeyFieldCodes(k, fieldCodeById), k.IsPrimary, k.IgnoreCase, k.IsActive))],
            definition.CodeMode);
    }

    /// <summary>Коди полів ключа в порядку частин.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="fieldCodeById">Коди полів довідника за ідентифікатором.</param>
    internal static List<string> KeyFieldCodes(RegistryKeyDef key, IReadOnlyDictionary<int, string> fieldCodeById)
        => [.. key.Fields
            .OrderBy(f => f.Ordinal)
            .Select(f => fieldCodeById.TryGetValue(f.RegistryFieldDefId, out var code)
                ? code
                : f.RegistryFieldDefId.ToString(CultureInfo.InvariantCulture))];

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

            var composition = field.RelationKind == RegistryRelationKind.Composition;

            relations.Add(new RegistryRelationDto(

                // ⚠ Поле, що вказує на ВЛАСНИЙ довідник, — це ієрархія
                // (`WasteGroup → WasteItem`), а не каскад: список звужує
                // батьківський запис того самого довідника. Композиція (RT-11,
                // `D-155`) — окремий вид: запис є ЧАСТИНОЮ батька, видимий і
                // видалюваний разом із ним.
                Kind: composition ? "Composition" : target == definition.Id ? "Hierarchy" : "Cascade",
                FieldCode: field.Code,
                TargetRegistryDefId: target,
                TargetRegistryCode: codesById.TryGetValue(target, out var targetCode) ? targetCode : null,
                LinkKind: null,
                LinkCount: null,
                OnParentDelete: composition ? field.OnParentDelete : null));
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
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{code}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = code });

        // ⛔ S18: заборона на довідник перекриває глобальне Registry.View — 404, як неіснуючий.
        RegistryAccess.EnsureNotDenied(profile, definition.Id, code);

        var own = await audit
            .ReadStructureChangesAsync(Types, definition.Id, Limit, ct)
            .ConfigureAwait(false);

        // ⛔ Перемикання master пишеться ОДНИМ записом на весь набір із
        // `EntityId = 0` (`ФВ-13.10`): сутності «група довідників» немає, і
        // зв'язок між перемкнутими разом довідниками живе саме в цьому записі.
        // Тому історія одного довідника без нього неповна — а показати
        // ЧУЖИЙ набір було б гірше за пропуск. Фільтр за кодом — у запиті, до
        // стелі (S18): «останні 100 перемикань, потім фільтр» губило свої, щойно
        // за ними набиралось сто чужих. Збіг — елемент масиву, а не підрядок:
        // код `WATER` міститься в `WATER_BODY`.
        var sets = await audit
            .ReadRegistrySetSwitchesAsync(definition.Code, Limit, ct)
            .ConfigureAwait(false);

        // ⛔ S18: запис набору називає й СУСІДІВ по набору; довідник під забороною для
        // викликача — невидимий, тож його код із запису прибирається.
        var hidden = await HiddenCodesAsync(profile, ct).ConfigureAwait(false);

        return own
            .Concat(sets.Where(s => Mentions(s.NewJson, definition.Code)))
            .OrderByDescending(c => c.ChangedAt)
            .Take(Limit)
            .Select(c => new RegistryHistoryEntryDto(
                c.ChangedAt, c.EntityType, c.Operation,
                c.EntityId == 0 ? Redact(c.OldJson, "registries", hidden) : c.OldJson,
                c.EntityId == 0 ? Redact(c.NewJson, "registryCodes", hidden) : c.NewJson,
                c.ChangeReason, c.ChangedByUserId))
            .ToList();
    }

    /// <summary>Коди довідників, схованих від викликача забороною.</summary>
    /// <param name="profile">Профіль доступу.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<HashSet<string>> HiddenCodesAsync(Security.AccessProfile profile, CancellationToken ct)
    {
        var denied = RegistryAccess.DeniedIds(profile);
        if (denied.Count == 0)
        {
            return [];
        }

        var all = await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
        return all
            .Where(d => denied.Contains(d.Id))
            .Select(d => d.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Прибирає зі списку <paramref name="property"/> запису набору довідники з <paramref name="hidden"/>:
    /// у <c>registryCodes</c> — рядки, у <c>registries</c> — об'єкти з полем <c>code</c>.
    /// </summary>
    /// <param name="json">Тіло запису аудиту.</param>
    /// <param name="property">Масив, у якому названо довідники.</param>
    /// <param name="hidden">Сховані коди.</param>
    private static string? Redact(string? json, string property, HashSet<string> hidden)
    {
        if (hidden.Count == 0 || string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root[property] is not JsonArray items)
            {
                return json;
            }

            foreach (var item in items.ToList())
            {
                var code = item is JsonObject named ? named["code"] : item;
                if (code is JsonValue value && value.TryGetValue<string>(out var text) && hidden.Contains(text))
                {
                    items.Remove(item);
                }
            }

            return root.ToJsonString();
        }
        catch (JsonException)
        {
            // ⚠ Нерозбірливий запис старішого формату: без певності, що в ньому немає схованого
            // коду, тіло не віддається.
            return null;
        }
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
    IClock clock,
    IUnitCatalog units,
    IRegistryKeyStore keys,
    Keys.RegistryKeyService keyService,
    Rules.RegistryRuleCompiler? ruleCompiler = null)
{
    // ⚠ `ruleCompiler` (RT-17a) необов'язковий лише для тестів, що будують обробник руками: їхні
    // правила — вирази до рушія (`[Limit] > 0`), і перевіряти їх граматикою правил там нічого.
    // Контейнер підставляє компілятор завжди; справжній шлях тримає `RegistryRulesHttpTests`.

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

        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ BE-24 крок 2: пряме збереження — це збереження І публікація одним
        // кроком, тож вимагає і права публікації. Інакше `Registry.Publish`
        // обходився б цим самим маршрутом.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, PublishRegistryDefinitionHandler.Permission, ct)
            .ConfigureAwait(false);

        var userId = RequireUser(currentUser);
        RequireReason(dto.Reason);
        RequireNoEmptyItems(dto.Fields, dto.Rules, dto.Keys);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw RegistryNotFound(code);

        // ⛔ S18: заборона на довідник виграє і над правом на опис — 404, як неіснуючий.
        RegistryAccess.EnsureNotDenied(profile, definition.Id, code);

        return await ApplyAsync(definition, dto, "SaveDefinition", userId, ct).ConfigureAwait(false);
    }

    internal static int RequireUser(ICurrentUser user)
        => user.UserId
           ?? throw new AccessDeniedException(
               "ECR-AUTH-0401",
               "Анонімний запит не змінює довідники.",
               new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

    internal static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Причина зміни опису обов'язкова: опис змінює те, як читаються вже збережені записи.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0422.definitionReasonRequired" });
        }
    }

    /// <summary>
    /// Порожній елемент (<c>null</c>) у полях, правилах чи ключах — відмова, а не
    /// <c>NullReferenceException</c> посеред застосування опису (прохід по відмовах 2).
    /// </summary>
    internal static void RequireNoEmptyItems<TField, TRule, TKey>(
        IReadOnlyList<TField>? fields, IReadOnlyList<TRule>? rules, IReadOnlyList<TKey>? keys)
    {
        if ((fields?.Any(f => f is null) ?? false)
            || (rules?.Any(r => r is null) ?? false)
            || (keys?.Any(k => k is null) ?? false))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Опис містить порожній елемент серед полів, правил чи ключів.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0422.definitionItemMissing" });
        }
    }

    internal static NotFoundException RegistryNotFound(string code)
        => new(
            "ECR-REG-0404",
            $"Довідника «{code}» не існує.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = code });

    /// <summary>
    /// Застосовує повний стан опису, пише журнал і зберігає — однією транзакцією.
    /// Спільне для прямого збереження і публікації чернетки.
    /// </summary>
    /// <param name="definition">Відстежуваний довідник.</param>
    /// <param name="dto">Поля, правила, причина.</param>
    /// <param name="operation">Дія в журналі: <c>SaveDefinition</c> або <c>PublishDefinition</c>.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<int> ApplyAsync(
        RegistryDef definition, SaveRegistryDefinitionDto dto, string operation, int userId, CancellationToken ct)
    {
        var rules = await registries.ListRulesAsync(definition.Id, ct).ConfigureAwait(false);
        var existingKeys = await keys.ListKeysForUpdateAsync(definition.Id, ct).ConfigureAwait(false);

        var before = Snapshot(definition, rules, KeySnapshots(definition, existingKeys, []));

        await RequireKnownUnitsAsync(dto.Fields, ct).ConfigureAwait(false);

        // Записи довідника читаються лише тоді, коли від них залежить рішення: режим коду й
        // обов'язковість нового поля. Решта збережень опису зайвого запиту не робить.
        IReadOnlyList<Domain.Entities.Dictionaries.RegistryEntry>? entries = null;
        async Task<IReadOnlyList<Domain.Entities.Dictionaries.RegistryEntry>> EntriesAsync()
            => entries ??= await registries.ListEntriesAsync(definition.Id, ct).ConfigureAwait(false);

        await ApplyCodeModeAsync(definition, dto.CodeMode, EntriesAsync).ConfigureAwait(false);

        // ⚠ RT-11: нове поле може бути обов'язковим, коли записів, які б його не мали, немає —
        // інакше поле композиції (обов'язкове за §4.8) і поля первинного ключа (обов'язкові за
        // D-153) не можна було б завести через HTTP узагалі.
        var newFieldsMayBeRequired = !dto.Fields.Any(f => f.Id is null && f.IsRequired)
            || !(await EntriesAsync().ConfigureAwait(false)).Any(e => !e.IsDeleted);

        // ⛔ ФВ-8.12 (порція 1): ціль посилання наявного поля змінюється лише поки на нього не посилається жодне значення.
        await GuardLookupRetargetAsync(definition, dto.Fields, EntriesAsync, ct).ConfigureAwait(false);

        ApplyFields(definition, dto.Fields, newFieldsMayBeRequired);

        IReadOnlyList<RegistryDef>? graph = null;

        // ⛔ RT-17a (§6, «Публікація опису»; Д-4): нові, змінені й знову ввімкнені правила
        // розбираються граматикою правил і перевіряються за формами довідників ДО збереження;
        // шаблон «Сума дочірніх» розгортається у вираз з параметрів. Неправильний вираз —
        // 422 `ruleExpressionInvalid` з діагностикою, а не правило, що «виглядає налаштованим».
        var wantedRules = dto.Rules;
        if (ruleCompiler is not null)
        {
            graph = await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
            if (wantedRules is { Count: > 0 })
            {
                wantedRules = await ruleCompiler
                    .PrepareAsync(definition, rules, wantedRules, graph, keys, ct)
                    .ConfigureAwait(false);
            }
        }

        var applied = ApplyRules(definition, rules, wantedRules);

        // ⛔ RT-12 (ФВ-8.16, §4.8): опис із композицією перевіряється цілим графом довідників —
        // цикл замикається через ІНШІ довідники, і знайти його в одному описі неможливо. Опис без
        // композиції нового ребра не додає, тож граф не читається (зайвий запит на кожне
        // збереження).
        if (definition.Fields.Any(f => f.RelationKind == RegistryRelationKind.Composition))
        {
            graph ??= await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
            RegistryCompositionRules.Validate(definition, graph);
        }

        var keyPlan = ApplyKeys(definition, existingKeys, dto.Keys);

        // ⛔ Обов'язковість перевіряється ПІСЛЯ застосування, на цілому описі:
        // довідник без жодного ключового поля не має бізнес-ключа, і його
        // записи неможливо зіставити ні з зовнішнім джерелом, ні між версіями.
        //
        // ⚠ RT-11 (§3.5): вимога послаблена до «≥ 1 IsKey АБО активний первинний ключ» — первинний
        // ключ і є бізнес-ключем запису, за ним шукає REGFIND.
        if (!definition.Fields.Any(f => f.IsKey) && !keyPlan.HasActivePrimary)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Довідник «{definition.Code}» лишився б без жодного ключового поля: "
                + "бізнес-ключ запису не було б із чого скласти.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.noKeyField",
                    ["registryCode"] = definition.Code,
                });
        }

        definition.BumpDefinitionVersion();

        // ⚠ Знімок «після» береться з ПАМ'ЯТІ, а не повторним читанням: щойно
        // додані правила ще не збережені, і запит до бази віддав би стан «до»
        // під виглядом стану «після» — тобто журнал, який мовчки бреше саме
        // там, де змін найбільше.
        var after = Snapshot(definition, applied, KeySnapshots(definition, existingKeys, keyPlan.Created));

        // ⛔ Q-244: аудит і `SaveChanges` тепер одна транзакція — до цієї
        // правки аудит писався сирим SQL без жодної відкритої транзакції, і
        // збій між ним і `SaveChangesAsync` лишав журнал і опис довідника
        // розсинхронізованими.
        //
        // ⛔ RT-11 (§4.5, п. 3): нові ключі, перевірка дублікатів і рядки `dic.RegistryEntryKey`
        // — у ТІЙ САМІЙ транзакції: опис із ключем без рядків ключів записи вже не захищав би, а
        // рядки без опису — не було б кому читати.
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
                    Operation: operation,
                    OldJson: before,
                    NewJson: after,
                    ChangeReason: dto.Reason,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            // Поля (і вимкнення старих ключів — раніше за вставку нового первинного, бо
            // `UX_RegistryKeyDef_Primary`) зберігаються першими: ключ посилається на збережене поле.
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            if (keyPlan.Created.Count > 0 || keyPlan.Reactivated.Count > 0)
            {
                await PublishKeysAsync(definition, keyPlan, userId, innerCt).ConfigureAwait(false);
            }

            // ⛔ RT-17a (§3.2, §6 «Момент»): ребра cfg.RegistryUse правил (SourceKind = 2) переписуються
            // цілком — ПІСЛЯ збереження правил (id нових відомі лише тепер) і в тій самій транзакції.
            // Змінене чи вимкнене правило не лишає застарілого ребра; «Де використано» (RT-19)
            // читає готові ребра.
            if (ruleCompiler is not null && graph is not null)
            {
                await registries
                    .ReplaceRuleUsesAsync(definition.Id, RuleUses(ruleCompiler, definition, applied, graph), innerCt)
                    .ConfigureAwait(false);
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        return definition.DefinitionVersion;
    }

    /// <summary>
    /// Ребра <c>cfg.RegistryUse</c> активних правил довідника (<c>SourceKind = 2</c>): що кожне читає.
    /// </summary>
    /// <remarks>
    /// ⚠ Вимкнене правило й <c>UniqueWithin</c> (не виконується, R-5) ребер не мають: вони нічого не
    /// читають. Код довідника, якого немає серед описів, ребра не дає — ключа на нього не буде.
    /// </remarks>
    private static List<RegistryUse> RuleUses(
        Rules.RegistryRuleCompiler compiler, RegistryDef definition, IReadOnlyList<RegistryRuleDef> rules, IReadOnlyList<RegistryDef> graph)
    {
        var idsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var registry in graph)
        {
            idsByCode[registry.Code] = registry.Id;
        }

        idsByCode[definition.Code] = definition.Id;

        var uses = new List<RegistryUse>();
        foreach (var rule in rules.Where(r => r.IsActive && r.RuleKind != RegistryRuleKind.UniqueWithin && r.Id > 0))
        {
            foreach (var (code, path) in compiler.UsesOf(rule, definition.Code))
            {
                if (idsByCode.TryGetValue(code, out var registryDefId) && registryDefId > 0)
                {
                    uses.Add(RegistryUse.ForRegistryRule(rule.Id, registryDefId, path));
                }
            }
        }

        return uses;
    }

    /// <summary>
    /// Створює нові ключі й наповнює рядки <c>dic.RegistryEntryKey</c> для ключів, що стали
    /// активними, — після перевірки дублікатів на наявних даних (§4.5).
    /// </summary>
    /// <exception cref="BusinessRuleException">
    /// <c>409 ECR-REG-4092 existingDuplicates</c>: на даних є записи з однаковим значенням ключа.
    /// </exception>
    private async Task PublishKeysAsync(RegistryDef definition, KeyPlan plan, int userId, CancellationToken ct)
    {
        var fill = new List<RegistryKeyDef>(plan.Reactivated);
        foreach (var pending in plan.Created)
        {
            var created = new RegistryKeyDef(
                definition.Id, EcrCode.Create(pending.Code), pending.Name, pending.Fields,
                pending.IsPrimary, pending.IgnoreCase, userId, clock.UtcNow);
            created.SetActive(pending.IsActive);
            keys.AddKey(created);

            if (pending.IsActive)
            {
                fill.Add(created);
            }
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        if (fill.Count == 0)
        {
            return;
        }

        var live = (await registries.ListEntriesAsync(definition.Id, ct).ConfigureAwait(false))
            .Where(e => !e.IsDeleted)
            .ToList();
        if (live.Count == 0)
        {
            return;
        }

        var fieldsById = definition.Fields.ToDictionary(f => f.Id);
        foreach (var key in fill)
        {
            var fields = key.Fields.OrderBy(f => f.Ordinal).Select(f => fieldsById[f.RegistryFieldDefId]).ToList();
            var scan = await Keys.RegistryKeyDuplicateScan
                .ScanAsync(registries, keys, definition, fields, key.IgnoreCase, ct)
                .ConfigureAwait(false);

            if (scan.Groups > 0)
            {
                throw Keys.RegistryKeyDuplicateScan.ExistingDuplicates(definition, key.Code, scan);
            }
        }

        // ⚠ Відстежувані записи: рядок ключа тримає навігацію на запис, і невідстежуваний запис EF
        // спробував би вставити наново. Рядки пише лише служба ключів (`D-151`).
        var tracked = (await registries
                .FindEntriesByCodesAsync(definition.Id, [.. live.Select(e => e.Code)], ct)
                .ConfigureAwait(false))
            .Where(e => !e.IsDeleted)
            .ToList();

        await keyService.ApplyAsync(definition, fill, tracked, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Застосовує режим коду записів (<c>D-157</c>).</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422 codeModeImmutable</c>: у довіднику вже є записи.</exception>
    private static async Task ApplyCodeModeAsync(
        RegistryDef definition,
        RegistryCodeMode? wanted,
        Func<Task<IReadOnlyList<Domain.Entities.Dictionaries.RegistryEntry>>> entries)
    {
        if (wanted is not { } mode || mode == definition.CodeMode)
        {
            return;
        }

        // ⛔ Лише поки записів немає ЖОДНИХ, і видалених теж: їхні коди лишаються в шкалі, і
        // перемикання дало б дві шкали кодів в одному довіднику — пошук запису за кодом при
        // імпорті перестав би бути однозначним (`RegistryDef.UseCodeMode`).
        if ((await entries().ConfigureAwait(false)).Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Режим коду записів довідника «{definition.Code}» не змінюється: у ньому вже є записи зі своїми кодами.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.codeModeImmutable",
                    ["registryCode"] = definition.Code,
                });
        }

        definition.UseCodeMode(mode);
    }

    /// <summary>
    /// Застосовує перелік ключів до опису (RT-11, §4.1): наявним — назва й активність, новим —
    /// перевірка складу. Самі нові ключі створюються в транзакції, коли їхні поля вже збережені.
    /// </summary>
    /// <param name="definition">Довідник (поля вже застосовано).</param>
    /// <param name="existing">Наявні ключі, відстежувані.</param>
    /// <param name="wanted">Ключі після правки; <c>null</c> — ключі не змінюються.</param>
    private static KeyPlan ApplyKeys(
        RegistryDef definition, IReadOnlyList<RegistryKeyDef> existing, IReadOnlyList<RegistryKeySaveDto>? wanted)
    {
        if (wanted is null)
        {
            return new KeyPlan([], [], existing.Any(k => k.IsActive && k.IsPrimary));
        }

        var byId = existing.ToDictionary(k => k.Id);
        var fieldCodeById = definition.Fields.Where(f => f.IsPersisted).ToDictionary(f => f.Id, f => f.Code);

        // ⛔ Ключ, якого немає в запиті, ВИМИКАЄТЬСЯ, а не зникає: на нього посилаються рядки
        // `dic.RegistryEntryKey`, а історія опису мусить пояснювати, чому колись діяла саме така
        // унікальність (так само, як із правилами).
        foreach (var id in byId.Keys.Except(wanted.Where(k => k.Id is not null).Select(k => k.Id!.Value)))
        {
            byId[id].SetActive(false);
        }

        var codes = existing.Select(k => k.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<PendingKey>();
        var reactivated = new List<RegistryKeyDef>();

        foreach (var dto in wanted)
        {
            if (dto.Id is { } id)
            {
                var key = byId.TryGetValue(id, out var found)
                    ? found
                    : throw new NotFoundException(
                        "ECR-REG-0404",
                        $"Ключа {id.ToString(CultureInfo.InvariantCulture)} у довіднику «{definition.Code}» немає.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0404.key",
                            ["keyId"] = id.ToString(CultureInfo.InvariantCulture),
                            ["registryCode"] = definition.Code,
                        });

                // ⛔ Склад, первинність і порівняння тексту не змінюються (`RegistryKeyDef`):
                // зміна мовчки перебудувала б хеш кожного запису.
                var fieldCodes = GetRegistryDefinitionHandler.KeyFieldCodes(key, fieldCodeById);
                if (!string.Equals(key.Code, dto.Code, StringComparison.Ordinal)
                    || key.IsPrimary != dto.IsPrimary
                    || key.IgnoreCase != dto.IgnoreCase
                    || !fieldCodes.SequenceEqual(dto.FieldCodes ?? [], StringComparer.OrdinalIgnoreCase))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Ключ «{key.Code}» не змінюється: заведіть новий ключ, а цей вимкніть.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0422.keyImmutable",
                            ["keyCode"] = key.Code,
                        });
                }

                // Повторне ввімкнення — те саме, що новий ключ: поки ключ не діяв, на даних могли
                // з'явитися дублікати, а поле первинного ключа — стати необов'язковим.
                if (!key.IsActive && dto.IsActive)
                {
                    if (key.IsPrimary)
                    {
                        _ = Keys.RegistryKeyFields.Resolve(definition, fieldCodes, isPrimary: true);
                    }

                    reactivated.Add(key);
                }

                key.Rename(dto.NameL10n);
                key.SetActive(dto.IsActive);
                continue;
            }

            var code = EcrCode.Create(dto.Code).Value;
            if (!codes.Add(code))
            {
                throw new BusinessRuleException(
                    "ECR-REG-0422",
                    $"Ключ із кодом «{code}» у довіднику «{definition.Code}» уже є.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REG-0422.keyCodeTaken",
                        ["keyCode"] = code,
                    });
            }

            var fields = Keys.RegistryKeyFields.Resolve(definition, dto.FieldCodes, dto.IsPrimary);
            created.Add(new PendingKey(code, dto.NameL10n, fields, dto.IsPrimary, dto.IgnoreCase, dto.IsActive));
        }

        var primaries = existing.Count(k => k.IsActive && k.IsPrimary) + created.Count(k => k.IsActive && k.IsPrimary);
        if (primaries > 1)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Довідник «{definition.Code}» мав би більше одного активного первинного ключа: REGFIND шукає рівно одним.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.primaryKeyTwice",
                    ["registryCode"] = definition.Code,
                });
        }

        return new KeyPlan(created, reactivated, primaries == 1);
    }

    /// <summary>Ключі для журналу: наявні (у поточному стані) і ще не створені.</summary>
    private static List<object> KeySnapshots(
        RegistryDef definition, IReadOnlyList<RegistryKeyDef> existing, IReadOnlyList<PendingKey> created)
    {
        var fieldCodeById = definition.Fields.Where(f => f.IsPersisted).ToDictionary(f => f.Id, f => f.Code);
        return
        [
            .. existing.Select(k => (object)new
            {
                k.Code,
                fields = GetRegistryDefinitionHandler.KeyFieldCodes(k, fieldCodeById),
                k.IsPrimary,
                k.IgnoreCase,
                k.IsActive,
            }),
            .. created.Select(k => (object)new
            {
                k.Code,
                fields = k.Fields.Select(f => f.Code).ToList(),
                k.IsPrimary,
                k.IgnoreCase,
                k.IsActive,
            }),
        ];
    }

    /// <summary>Що зробити з ключами в транзакції збереження.</summary>
    /// <param name="Created">Нові ключі — створюються, коли їхні поля збережені.</param>
    /// <param name="Reactivated">Наявні ключі, що знову стали активними.</param>
    /// <param name="HasActivePrimary">Чи матиме довідник активний первинний ключ.</param>
    private sealed record KeyPlan(
        IReadOnlyList<PendingKey> Created, IReadOnlyList<RegistryKeyDef> Reactivated, bool HasActivePrimary);

    /// <summary>Новий ключ до створення.</summary>
    private sealed record PendingKey(
        string Code,
        LocalizedText Name,
        IReadOnlyList<RegistryFieldDef> Fields,
        bool IsPrimary,
        bool IgnoreCase,
        bool IsActive);

    /// <summary>Відмовляє, якщо одиниці якогось поля в довіднику одиниць немає (HSE301 U1).</summary>
    /// <remarks>
    /// ⛔ Доти неіснуючий <c>UnitId</c> поля записувався мовчки: ключа на
    /// <c>uom.Unit</c> не було. Тепер <c>FK_RegField_Unit</c> є, і без цієї перевірки
    /// описка давала б голий <c>500</c> на 547 замість <c>422</c> з ключем. Перевіряється
    /// ДО застосування полів, одним знімком довідника на весь опис; спільне для
    /// прямого збереження і публікації чернетки (обидва йдуть через
    /// <see cref="ApplyAsync"/>).
    /// </remarks>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422</c>, ключ <c>unknownUnit</c>.</exception>
    private async Task RequireKnownUnitsAsync(IReadOnlyList<RegistryFieldSaveDto> fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var withUnit = fields.Where(f => f.UnitId is not null).ToList();
        if (withUnit.Count == 0)
        {
            return;
        }

        var catalogue = await units.GetAsync(ct).ConfigureAwait(false);
        var known = catalogue.Units.Values.Select(u => u.Id).ToHashSet();

        var unknown = withUnit.FirstOrDefault(f => !known.Contains(f.UnitId!.Value));
        if (unknown is null)
        {
            return;
        }

        var unitId = unknown.UnitId!.Value.ToString(CultureInfo.InvariantCulture);
        throw new BusinessRuleException(
            "ECR-REG-0422",
            $"Поле «{unknown.Code}»: одиниці {unitId} у довіднику немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REG-0422.unknownUnit",
                ["fieldCode"] = unknown.Code,
                ["unitId"] = unitId,
            });
    }

    /// <summary>
    /// Змінює або знімає ціль посилання НАЯВНОГО поля (<c>ФВ-8.12</c>): тільки коли жодне
    /// збережене значення цього поля не вказує на запис (<c>ValueRefEntryId</c>) — інакше комірки
    /// стали б посиланнями в чужий довідник.
    /// </summary>
    /// <remarks>
    /// ⚠ Композиція і ключове поле не перенацілюються (відношення й бізнес-ключ незмінні).
    /// Усі відмови — <c>ECR-REG-0422</c> з ключем; нового коду немає. Аудит пише
    /// <c>ApplyAsync</c>: знімок опису містить <c>RefRegistryDefId</c> до і після.
    /// </remarks>
    private async Task GuardLookupRetargetAsync(
        RegistryDef definition,
        IReadOnlyList<RegistryFieldSaveDto> wanted,
        Func<Task<IReadOnlyList<Domain.Entities.Dictionaries.RegistryEntry>>> entriesAsync,
        CancellationToken ct)
    {
        var changed = new List<(RegistryFieldDef Field, int? Target)>();
        foreach (var w in wanted)
        {
            if (w.Id is not { } id
                || definition.Fields.FirstOrDefault(f => f.Id == id) is not { DataType: CellDataType.Lookup } field
                || field.RefRegistryDefId == w.LookupRegistryDefId)
            {
                continue;
            }

            if (field.RelationKind == RegistryRelationKind.Composition || field.IsKey)
            {
                throw Retarget("err.ECR-REG-0422.relationKindImmutable", field.Code);
            }

            if (w.LookupRegistryDefId is { } target
                && await registries.FindDefinitionByIdAsync(target, ct).ConfigureAwait(false) is null)
            {
                throw Retarget("err.ECR-REG-0422.lookupTargetUnknown", field.Code);
            }

            changed.Add((field, w.LookupRegistryDefId));
        }

        if (changed.Count == 0)
        {
            return;
        }

        // ⛔ Правила, формули й методології, що йдуть через поле (`FIELD.attr`): тип шляху
        // виводиться з цілі посилання, тож після перенацілення вони тихо посилалися б на атрибут,
        // якого в новій цілі немає (про це дізнались би лише під час виконання). Відмова з переліком.
        var consumers = await registries
            .FindFieldChainConsumersAsync(
                definition.Id, changed.Select(c => (string)c.Field.Code).ToList(), UsageResponse.PageSize, ct)
            .ConfigureAwait(false);
        if (consumers.Total > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Зв'язок поля «{changed[0].Field.Code}» не можна змінити: через нього читають атрибути правил, формул чи методологій — {consumers.Total.ToString(CultureInfo.InvariantCulture)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.lookupRetargetUsedByRules",
                    ["fieldCode"] = (string)changed[0].Field.Code,
                    ["total"] = consumers.Total.ToString(CultureInfo.InvariantCulture),
                    ["usedBy"] = string.Join(", ", consumers.Items.Select(i => i.Label)),
                    ["references"] = consumers.Items,
                });
        }

        var entries = await entriesAsync().ConfigureAwait(false);
        var fieldIds = changed.Select(c => c.Field.Id).ToHashSet();
        var values = entries.Count == 0
            ? []
            : await registries.ListValuesForEntriesAsync(entries.Select(e => e.Id).ToList(), ct).ConfigureAwait(false);
        if (values.FirstOrDefault(v => v.ValueRefEntryId is not null && fieldIds.Contains(v.RegistryFieldDefId)) is { } used)
        {
            throw Retarget("err.ECR-REG-0422.lookupRetargetInUse", changed.First(c => c.Field.Id == used.RegistryFieldDefId).Field.Code);
        }

        foreach (var (field, target) in changed)
        {
            field.PointTo(target);
        }
    }

    private static BusinessRuleException Retarget(string messageKey, string fieldCode)
        => new(
            "ECR-REG-0422",
            $"Зв'язок поля «{fieldCode}» не можна змінити.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                ["fieldCode"] = fieldCode,
            });
    /// <summary>Застосовує перелік полів до опису.</summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="wanted">Поля після правки.</param>
    /// <param name="newFieldsMayBeRequired">
    /// Чи може нове поле бути обов'язковим: так, лише коли в довіднику немає живих записів.
    /// </param>
    private static void ApplyFields(
        RegistryDef definition, IReadOnlyList<RegistryFieldSaveDto> wanted, bool newFieldsMayBeRequired)
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
                + $"Полів у запиті бракує: {dropped.Count.ToString(CultureInfo.InvariantCulture)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.fieldRemoved",
                    ["missingCount"] = dropped.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        foreach (var field in wanted)
        {
            if (field.Id is { } id)
            {
                if (!existing.TryGetValue(id, out var target))
                {
                    throw new NotFoundException(
                        "ECR-REG-0404",
                        $"Поля {id.ToString(CultureInfo.InvariantCulture)} у довіднику «{definition.Code}» немає.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0404.field",
                            ["fieldId"] = id.ToString(CultureInfo.InvariantCulture),
                            ["registryCode"] = definition.Code,
                        });
                }

                // ⛔ Код і тип наявного поля не змінюються — див. XML-doc
                // `RegistryFieldDef.Update`. Розбіжність тут означає, що клієнт
                // надіслав чуже поле під наявним Id, і мовчки застосувати з
                // цього лише підпис було б гірше за відмову.
                if (!string.Equals(target.Code, field.Code, StringComparison.Ordinal))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Код поля «{target.Code}» не змінюється: на нього посилаються вирази і мапінг.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0422.fieldCodeImmutable",
                            ["fieldCode"] = target.Code,
                        });
                }

                if (!string.Equals(target.DataType.ToString(), field.DataType, StringComparison.Ordinal))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Тип поля «{target.Code}» не змінюється: він визначає, як читаються вже збережені значення.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0422.fieldTypeImmutable",
                            ["fieldCode"] = target.Code,
                        });
                }

                // ⛔ RT-11: відношення (посилання чи композиція) наявного поля не змінюється —
                // `RegistryFieldDef.ComposeInto`: наявні записи раптом стали б частинами батька,
                // невидимими без нього й видалюваними разом із ним. `null` — «без змін».
                if ((field.RelationKind is { } kind && kind != target.RelationKind)
                    || (target.RelationKind == RegistryRelationKind.Composition
                        && field.OnParentDelete is { } policy && policy != target.OnParentDelete))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Відношення поля «{target.Code}» до батька не змінюється: заведіть нове поле.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0422.relationKindImmutable",
                            ["fieldCode"] = target.Code,
                        });
                }

                target.Update(field.NameL10n, field.Ordinal, field.IsRequired);

                // ⛔ Одиниця вимірювання ЗМІНЮЄТЬСЯ і для наявного поля (аудит
                // 2026-09-16, §4.3). `MeasureIn` викликався лише для НОВИХ
                // полів, тож `PUT .../registries/{code}/definition` із
                // виправленою одиницею повертав 200 і нову DefinitionVersion,
                // аудит відображав НАМІР нового значення — а `target.UnitId`
                // фактично не змінювався, і всі споживачі нижче (перевірки
                // сумісності одиниць, відображення) далі брали старе, неправильне.
                //
                // ⚠ `UnitId` серед заморожених полів `RegistryFieldDef.Update`
                // не перелічений, і `MeasureIn` — на відміну від
                // `MarkKey`/`PointTo`, явно позначених «⚠ Лише під час
                // створення» — такого обмеження не має. Тобто домен це дозволяв;
                // обробник просто не кликав.
                target.MeasureIn(field.UnitId);
                continue;
            }

            if (!Enum.TryParse<CellDataType>(field.DataType, ignoreCase: false, out var dataType))
            {
                throw new BusinessRuleException(
                    "ECR-REG-0422",
                    $"Тип поля «{field.DataType}» не існує.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REG-0422.unknownFieldType",
                        ["dataType"] = field.DataType,
                    });
            }

            var created = new RegistryFieldDef(
                definition.Id, EcrCode.Create(field.Code), field.NameL10n, dataType, field.Ordinal);

            created.Update(field.NameL10n, field.Ordinal, field.IsRequired);
            created.MarkKey(field.IsKey);
            created.PointTo(field.LookupRegistryDefId);
            created.MeasureIn(field.UnitId);

            // ⛔ RT-11 (D-155): композиція — через `Compose`, а не `ComposeInto` напряму: поле не
            // Lookup — це введення людини, і відмова мусить бути 422 з ключем, а не
            // InvalidOperationException домену (500).
            if (field.RelationKind == RegistryRelationKind.Composition)
            {
                RegistryCompositionRules.Compose(created, field.OnParentDelete ?? ParentDeletePolicy.Restrict);
            }

            // ⚠ Нове поле обов'язковим бути НЕ може, поки в довіднику є записи:
            // вони його не мають, і вимога значення зробила б увесь довідник
            // недійсним одразу після збереження. Заповнити його треба спершу даними.
            if (field.IsRequired && !newFieldsMayBeRequired)
            {
                throw new BusinessRuleException(
                    "ECR-REG-0422",
                    $"Нове поле «{field.Code}» не може бути обов'язковим: наявні записи його не мають. "
                    + "Заведіть його необов'язковим, заповніть і лише тоді вимагайте.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REG-0422.newFieldRequired",
                        ["fieldCode"] = field.Code,
                    });
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
                    "ECR-REG-0422",
                    $"Рівень «{rule.Severity}» не існує.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REG-0422.unknownSeverity",
                        ["severity"] = rule.Severity,
                    });
            }

            if (rule.Id is { } id)
            {
                if (!byId.TryGetValue(id, out var target))
                {
                    throw new NotFoundException(
                        "ECR-REG-0404",
                        $"Правила {id.ToString(CultureInfo.InvariantCulture)} у довіднику «{definition.Code}» немає.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0404.rule",
                            ["ruleId"] = id.ToString(CultureInfo.InvariantCulture),
                            ["registryCode"] = definition.Code,
                        });
                }

                // ⛔ Вид правила не змінюється: параметри і предикат означають
                // для кожного виду різне, і зміна виду при збережених
                // параметрах дала б правило, яке перевіряє не те.
                if (!string.Equals(target.RuleKind.ToString(), rule.RuleKind, StringComparison.Ordinal))
                {
                    throw new BusinessRuleException(
                        "ECR-REG-0422",
                        $"Вид правила «{target.Code}» не змінюється: заведіть нове правило потрібного виду.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0422.ruleKindImmutable",
                            ["ruleCode"] = target.Code,
                        });
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
                    + "RequiredWhen, UniqueWithin, Expression, CrossRegistry.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REG-0422.unknownRuleKind",
                        ["ruleKind"] = rule.RuleKind,
                    });
            }

            // ⛔ RT-17a (`R-5`, `D-154`, §4.7): унікальність задає ключ довідника, а не правило. Нове
            // `UniqueWithin` не приймається; наявні лишаються (їх пропонують перетворити на ключ) і
            // не виконуються — на даних можуть бути дублікати, яких правило ніколи не ловило.
            if (kind == RegistryRuleKind.UniqueWithin)
            {
                throw Rules.RegistryRuleCompiler.UniqueWithinReplaced(rule.Code);
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
    /// <param name="keys">Його ключі (RT-11).</param>
    private static string Snapshot(RegistryDef definition, IReadOnlyList<RegistryRuleDef> rules, IReadOnlyList<object> keys)
        => JsonSerializer.Serialize(new
        {
            definitionVersion = definition.DefinitionVersion,
            codeMode = definition.CodeMode.ToString(),
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
                    relationKind = f.RelationKind.ToString(),
                    onParentDelete = f.RelationKind == RegistryRelationKind.Composition ? f.OnParentDelete.ToString() : null,
                })
                .ToList(),
            keys,
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

/// <summary>
/// Обмеження опису композиції довідників (<c>ФВ-8.16</c>, <c>D-155</c>,
/// FEATURE-REGISTRY-TABLES §4.8) — відмови <c>ECR-REG-0422</c> з ключем для людини.
/// </summary>
/// <remarks>
/// ⛔ Домен тримає лише інваріант типу (<see cref="RegistryFieldDef.ComposeInto"/> кидає
/// <see cref="InvalidOperationException"/> — це помилка коду, а не введення, і без перевірки тут
/// вона доїхала б до клієнта як 500). Решта правил — властивості ГРАФА довідників, яких один опис
/// не бачить: одна композиція на довідник, ціль — інший довідник, поле обов'язкове, дочірній
/// довідник нетемпоральний, циклів немає.
/// </remarks>
public static class RegistryCompositionRules
{
    /// <summary>
    /// Робить поле частиною композиції — після перевірки типу, яка дає <c>422</c> ДО домену.
    /// </summary>
    /// <param name="field">Нове поле опису.</param>
    /// <param name="onParentDelete">Що робити з частиною, коли видаляють батька.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422</c>, ключ <c>compositionNotLookup</c>.</exception>
    /// <remarks>
    /// Точка виклику — <c>SaveRegistryDefinitionHandler.ApplyFields</c> для нового поля з
    /// <c>relationKind = Composition</c> (RT-11).
    /// </remarks>
    public static void Compose(RegistryFieldDef field, ParentDeletePolicy onParentDelete)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.DataType != CellDataType.Lookup)
        {
            throw NotLookup(field);
        }

        field.ComposeInto(onParentDelete);
    }

    /// <summary>Перевіряє композицію опису на тлі всіх довідників.</summary>
    /// <param name="definition">Опис після застосування правки (у пам'яті, ще не збережений).</param>
    /// <param name="registries">
    /// Усі довідники (збережені); збережена копія <paramref name="definition"/> заміщується
    /// версією з пам'яті.
    /// </param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422</c>, ключі <c>composition*</c>.</exception>
    public static void Validate(RegistryDef definition, IReadOnlyList<RegistryDef> registries)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registries);

        var compositions = Compositions(definition).ToList();
        if (compositions.Count == 0)
        {
            return;
        }

        // Масовий імпорт іде повз домен (`CK_RegField_Composition` тримає базу); тут — щоб опис,
        // який таки дійшов сюди, відмовив словами, а не порушенням обмеження.
        if (compositions.FirstOrDefault(f => f.DataType != CellDataType.Lookup) is { } notLookup)
        {
            throw NotLookup(notLookup);
        }

        if (compositions.Count > 1)
        {
            var fields = string.Join(", ", compositions.Select(f => f.Code));
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Довідник «{definition.Code}» має більше одного поля композиції ({fields}): запис може бути частиною лише одного батька.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.compositionMoreThanOne",
                    ["registryCode"] = definition.Code,
                    ["fields"] = fields,
                });
        }

        var field = compositions[0];
        if (field.RefRegistryDefId is not { } target || target == definition.Id)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Поле композиції «{field.Code}» мусить указувати на інший довідник: ієрархія в межах одного — це батьківський запис.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.compositionTargetSelf",
                    ["fieldCode"] = field.Code,
                });
        }

        if (!field.IsRequired)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Поле композиції «{field.Code}» мусить бути обов'язковим: частина без батька не видна ніколи.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.compositionNotRequired",
                    ["fieldCode"] = field.Code,
                });
        }

        if (definition.IsTemporal)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Довідник «{definition.Code}» — частина іншого і не може мати власного вікна чинності: його записи видно рівно тоді, коли видно батька.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.compositionChildTemporal",
                    ["registryCode"] = definition.Code,
                });
        }

        if (FindCycle(definition, registries) is { } chain)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Композиція замикається в коло: {chain}. Довідник не може бути частиною самого себе, навіть через інші.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.compositionCycle",
                    ["chain"] = chain,
                });
        }
    }

    /// <summary>
    /// Коло композиції, що проходить через <paramref name="definition"/>: пошук у глибину по
    /// ребрах «дитина → батько»; <c>null</c> — кола немає.
    /// </summary>
    /// <returns>Ланцюжок кодів <c>A → B → A</c>.</returns>
    /// <remarks>
    /// ⚠ Шукається лише коло, що проходить через цей опис: саме його може додати правка. Коло
    /// між іншими довідниками (дані повз опис) обхід пропускає, а не зациклюється.
    /// </remarks>
    private static string? FindCycle(RegistryDef definition, IReadOnlyList<RegistryDef> registries)
    {
        var byId = new Dictionary<int, RegistryDef>();
        foreach (var registry in registries)
        {
            byId[registry.Id] = registry;
        }

        byId[definition.Id] = definition;

        var visited = new HashSet<int>();
        var path = new List<string> { definition.Code };

        string? Walk(RegistryDef node)
        {
            foreach (var edge in Compositions(node))
            {
                if (edge.RefRegistryDefId is not { } parentId)
                {
                    continue;
                }

                if (parentId == definition.Id)
                {
                    return string.Join(" → ", path.Append(definition.Code));
                }

                if (!visited.Add(parentId) || !byId.TryGetValue(parentId, out var parent))
                {
                    continue;
                }

                path.Add(parent.Code);
                if (Walk(parent) is { } found)
                {
                    return found;
                }

                path.RemoveAt(path.Count - 1);
            }

            return null;
        }

        return Walk(definition);
    }

    private static IEnumerable<RegistryFieldDef> Compositions(RegistryDef registry)
        => registry.Fields.Where(f => f.RelationKind == RegistryRelationKind.Composition);

    private static BusinessRuleException NotLookup(RegistryFieldDef field)
        => new(
            "ECR-REG-0422",
            $"Поле «{field.Code}» має тип {field.DataType}: частиною іншого довідника запис робить лише поле Lookup.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REG-0422.compositionNotLookup",
                ["fieldCode"] = field.Code,
                ["dataType"] = field.DataType.ToString(),
            });
}




