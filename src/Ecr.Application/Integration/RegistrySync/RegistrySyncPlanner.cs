// src/Ecr.Application/Integration/RegistrySync/RegistrySyncPlanner.cs
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.RegistrySync;

/// <summary>
/// Чиста логіка синхронізації довідника з зовнішнім джерелом (PI AF):
/// знімок джерела + зв'язки + поточні значення + мапінги → план
/// (<c>ФВ-8.10</c>, <c>ФВ-8.11</c>; <c>docs/build/FEATURE-REGISTRY-SYNC.md</c>, крок S4).
/// </summary>
/// <remarks>
/// ⛔ Без бази й без портів запису. Планувальник НЕ пише: записує
/// <c>RegistrySyncJob</c> через спільний <c>RegistryEntryWriter</c> (крок S7), а
/// до того задача лише звіряє (S5). Тому все, що тут вирішується, перевіряється
/// юніт-тестом без СУБД.
/// <para>
/// Політика за <see cref="RegistrySourceKind"/> (<c>ФВ-8.9</c>, <c>D-202</c>):
/// </para>
/// <list type="bullet">
/// <item><c>External</c> — синк володіє всіма змапленими полями; поле з
/// ВИМКНЕНИМ мапінгом не пишеться, розбіжність іде подією
/// <see cref="RegistrySyncEventKind.Diverged"/> (запис через мапінг, який
/// адміністратор свідомо вимкнув, перетер би його рішення).</item>
/// <item><c>Hybrid</c> — синк володіє лише полями з АКТИВНИМ мапінгом; решта
/// полів — локальні, синк про них мовчить.</item>
/// <item><c>Local</c> — лише звірка (<c>D-49</c>): жодного оновлення, розбіжність —
/// <see cref="RegistrySyncEventKind.Diverged"/>.</item>
/// </list>
/// <para>
/// ⚠ Ручна правка перемагає лише в <c>Hybrid</c> (<c>D-118</c>, <c>D-212</c> (2)):
/// якщо останнім поле записала людина, а джерело каже інше —
/// <see cref="RegistrySyncEventKind.ConflictKeptManual"/>, без запису. У
/// <c>External</c> ручного запису немає (<c>D-211</c>) — синк перезаписує.
/// Зниклий елемент — за <c>OnMissingInSource</c> (<see cref="PlanMissing"/>), запис не
/// видаляється; його повернення — <see cref="RegistrySyncPlan.MissingClears"/> і для
/// <c>External</c> — <see cref="RegistrySyncPlan.Reactivations"/>. Перестворений у AF
/// елемент (новий GUID, той самий шлях) — <see cref="RegistrySyncPlan.Relinks"/>. Новий елемент без
/// зв'язку: <c>External</c> на повному знімку — автостворення
/// (<see cref="RegistrySyncPlan.Creates"/>, <c>D-212</c> (1), Q4), інакше — лише подія.
/// </para>
/// <para>
/// ⚠ Поле <c>Lookup</c> з <see cref="RegistrySyncFieldMapping.RefRegistryDefId"/>
/// приходить із джерела КОДОМ (<c>D-212</c> (5)): задача спершу бере коди з
/// <see cref="LookupCodes"/>, розв'язує їх у базі й передає в
/// <see cref="RegistrySyncInput.LookupCodes"/>; коду немає —
/// <see cref="RegistrySyncEventKind.ValueRejected"/> (<c>entryRefNotFound</c>), поле
/// не чіпається.
/// </para>
/// </remarks>
public static class RegistrySyncPlanner
{
    /// <summary>Код відмови «значення не приводиться до поля».</summary>
    public const string ValueRejectedCode = "ECR-REG-0422";

    /// <summary>Ключ каталогу: у довіднику, на який посилається поле, немає запису з таким кодом.</summary>
    public const string EntryRefNotFoundKey = "err.ECR-REG-0422.entryRefNotFound";

    /// <summary>
    /// «Поле» події <see cref="RegistrySyncEventKind.Diverged"/> про ввімкненість запису
    /// (<c>Hybrid</c>, <c>D-212</c> Q6) — як <c>@active</c> в аудиті writer'а.
    /// </summary>
    public const string ActiveFieldCode = "@active";

    private static readonly IReadOnlyDictionary<string, long> NoCodes = new Dictionary<string, long>();

    /// <summary>
    /// Коди записів інших довідників, які задача має розв'язати в <c>Id</c> перед
    /// <see cref="Plan"/>: значення атрибутів знімка для мапінгів <c>Lookup</c> з
    /// <see cref="RegistrySyncFieldMapping.RefRegistryDefId"/>.
    /// </summary>
    /// <param name="input">Той самий вхід, що піде в <see cref="Plan"/> (без <see cref="RegistrySyncInput.LookupCodes"/>).</param>
    /// <returns>Різні пари «довідник, код» в усталеному порядку; порожні значення не входять.</returns>
    public static IReadOnlyList<RegistrySyncLookupCode> LookupCodes(RegistrySyncInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var byCode = input.Mappings.Where(m => m.RefRegistryDefId is not null).ToList();
        var codes = new HashSet<RegistrySyncLookupCode>();

        foreach (var element in input.Elements)
        {
            foreach (var mapping in byCode)
            {
                if (element.Attributes.TryGetValue(mapping.SourceAttribute, out var raw) && CodeOf(raw) is { } code)
                {
                    codes.Add(new RegistrySyncLookupCode(mapping.RefRegistryDefId!.Value, code));
                }
            }
        }

        return [.. codes.OrderBy(c => c.RegistryDefId).ThenBy(c => c.Code, StringComparer.Ordinal)];
    }

    /// <summary>Будує план синхронізації.</summary>
    /// <param name="input">Знімок джерела й стан довідника.</param>
    /// <returns>План; <see cref="RegistrySyncPlan.IsEmpty"/> — якщо нічого не змінилося.</returns>
    /// <exception cref="ArgumentException">
    /// Дубль <c>ExternalId</c> у знімку або в зв'язках: це порушення контракту
    /// читача, а не дані, які можна «якось» спланувати.
    /// </exception>
    public static RegistrySyncPlan Plan(RegistrySyncInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // GUID/WebId AF регістронезалежні: той самий елемент, записаний іншим
        // регістром, не має ставати «зниклим» і «неприв'язаним» одночасно.
        var elements = ToUniqueMap(input.Elements, e => e.ExternalId, nameof(input.Elements));
        var links = ToUniqueMap(input.Links, l => l.ExternalId, nameof(input.Links));
        var entries = input.Entries.ToDictionary(e => e.RegistryEntryId);
        var mappings = input.Mappings
            .OrderBy(m => m.RegistryFieldDefId)
            .ThenBy(m => m.SourceAttribute, StringComparer.Ordinal)
            .ToList();

        var writes = input.SourceKind != RegistrySourceKind.Local;
        var updates = new List<RegistrySyncUpdate>();
        var paths = new List<RegistrySyncPathChange>();
        var events = new List<RegistrySyncEvent>();
        var creates = new List<RegistrySyncCreate>();
        var relinks = new List<RegistrySyncRelink>();
        var marks = new List<RegistrySyncLinkMark>();
        var clears = new List<RegistrySyncLinkMark>();
        var deactivations = new List<RegistrySyncActivation>();
        var reactivations = new List<RegistrySyncActivation>();

        // D-212 (7): зіставлення за ExternalPath — ДО обходу елементів: інакше кандидат
        // став би «новим» (автостворення в External — дубль запису). Лише повний знімок:
        // у неповному «старого GUID немає» нічого не доводить. Local не пише — не зіставляє.
        var (takenLinks, takenElements) = writes && input.IsCompleteSnapshot
            ? MatchByPath(elements, links, relinks)
            : (new Dictionary<string, bool>(), new Dictionary<string, bool>());

        foreach (var element in elements.Values.OrderBy(e => e.ExternalId, StringComparer.Ordinal))
        {
            if (!links.TryGetValue(element.ExternalId, out var link))
            {
                if (takenElements.TryGetValue(element.ExternalId, out var relinked))
                {
                    // Перепривʼязаний — план у Relinks. Неоднозначний — нічого не пишемо,
                    // лише подія: котрий із кандидатів «той самий», вирішує людина.
                    if (!relinked)
                    {
                        events.Add(new RegistrySyncEvent(RegistrySyncEventKind.ElementUnlinked, element.ExternalId, null));
                    }

                    continue;
                }

                // D-212 (1): External на ПОВНОМУ знімку створює запис. Неповний знімок
                // автостворення не дає: елемент, чий старий GUID не прочитався, став би
                // дублем уже наявного запису.
                if (input.SourceKind == RegistrySourceKind.External
                    && input.IsCompleteSnapshot
                    && PlanCreate(input, element, mappings, events) is { } create)
                {
                    creates.Add(create);
                }
                else
                {
                    events.Add(new RegistrySyncEvent(RegistrySyncEventKind.ElementUnlinked, element.ExternalId, null));
                }

                continue;
            }

            // Елемент знову є: позначку зникнення знято (сам зв'язок живий, хоч би що з записом).
            if (writes && link.MissingInSourceSince is not null)
            {
                clears.Add(new RegistrySyncLinkMark(link.ExternalId, link.RegistryEntryId));
            }

            // Запис, якого немає в стані (видалений логічно чи поза вибіркою), не
            // плануємо: писати в нього синк однаково не має права.
            if (!entries.TryGetValue(link.RegistryEntryId, out var entry))
            {
                continue;
            }

            // D-212 Q6: повернення після Deactivate — вимкнений запис зі знятою тепер
            // позначкою. External вмикає сам; Hybrid — лише подія, вмикає людина.
            // Вимкнений без позначки вимкнула людина — синк його не чіпає.
            if (writes && link.MissingInSourceSince is not null && !entry.IsActive)
            {
                if (input.SourceKind == RegistrySourceKind.External)
                {
                    reactivations.Add(new RegistrySyncActivation(entry.RegistryEntryId, element.ExternalId));
                }
                else
                {
                    events.Add(new RegistrySyncEvent(
                        RegistrySyncEventKind.Diverged, element.ExternalId, entry.RegistryEntryId,
                        ActiveFieldCode, CurrentValue: false, SourceValue: true));
                }
            }

            if (element.ExternalPath is { } path && !string.Equals(path, link.ExternalPath, StringComparison.Ordinal))
            {
                paths.Add(new RegistrySyncPathChange(element.ExternalId, entry.RegistryEntryId, link.ExternalPath, path));
            }

            foreach (var mapping in mappings)
            {
                PlanField(input, element, entry, mapping, updates, events);
            }
        }

        if (input.IsCompleteSnapshot)
        {
            foreach (var link in links.Values.OrderBy(l => l.ExternalId, StringComparer.Ordinal))
            {
                if (elements.ContainsKey(link.ExternalId))
                {
                    continue;
                }

                if (takenLinks.TryGetValue(link.ExternalId, out var relinked))
                {
                    // Неоднозначна перепривʼязка — лише подія, без позначки й вимкнення.
                    if (!relinked)
                    {
                        events.Add(new RegistrySyncEvent(
                            RegistrySyncEventKind.SourceMissing, link.ExternalId, link.RegistryEntryId));
                    }

                    continue;
                }

                PlanMissing(input, link, entries, events, marks, deactivations);
            }
        }

        return new RegistrySyncPlan(updates, paths, events)
        {
            Creates = creates,
            Relinks = relinks,
            MissingMarks = marks,
            MissingClears = clears,
            Deactivations = deactivations,
            Reactivations = reactivations,
        };
    }

    /// <summary>
    /// Зниклий із повного знімка елемент за <see cref="RegistrySyncInput.OnMissingInSource"/>
    /// (<c>D-212</c> (3), Q5). Запис не видаляється ніколи.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>MarkOrphaned</c> — позначка зв'язку (якщо ще немає) + подія <c>SourceMissing</c>.</item>
    /// <item><c>Deactivate</c> — позначка + вимкнення запису; подію <c>Deactivated</c> пише
    /// виконавець після успіху. ⚠ <c>Hybrid</c>: запис, який людина ввімкнула вже ПІСЛЯ
    /// позначки, синк не вимикає вдруге — людина виграє (<c>D-212</c> (2)); <c>External</c>
    /// вимикає щоразу, коли запис увімкнено.</item>
    /// <item><c>Ignore</c> — запис і зв'язок не чіпаються, подія <c>SourceMissing</c> пишеться (Q5).</item>
    /// <item><c>Local</c> — лише подія: звірка нічого не пише (<c>D-49</c>).</item>
    /// </list>
    /// </remarks>
    private static void PlanMissing(
        RegistrySyncInput input,
        RegistrySyncLink link,
        Dictionary<long, RegistrySyncEntryState> entries,
        List<RegistrySyncEvent> events,
        List<RegistrySyncLinkMark> marks,
        List<RegistrySyncActivation> deactivations)
    {
        var missing = new RegistrySyncEvent(RegistrySyncEventKind.SourceMissing, link.ExternalId, link.RegistryEntryId);

        if (input.SourceKind == RegistrySourceKind.Local || input.OnMissingInSource == RegistryMissingPolicy.Ignore)
        {
            events.Add(missing);
            return;
        }

        var alreadyMarked = link.MissingInSourceSince is not null;
        if (!alreadyMarked)
        {
            marks.Add(new RegistrySyncLinkMark(link.ExternalId, link.RegistryEntryId));
        }

        if (input.OnMissingInSource == RegistryMissingPolicy.Deactivate)
        {
            if (entries.TryGetValue(link.RegistryEntryId, out var entry)
                && entry.IsActive
                && (input.SourceKind == RegistrySourceKind.External || !alreadyMarked))
            {
                deactivations.Add(new RegistrySyncActivation(link.RegistryEntryId, link.ExternalId));
            }

            return;
        }

        events.Add(missing);
    }

    /// <summary>
    /// Зіставлення за запасним ключем <c>ExternalPath</c> (<c>D-212</c> (7)): зниклий зі
    /// знімка зв'язок і неприв'язаний елемент із тим самим шляхом. Рівно один на один —
    /// перепривʼязка; інакше — неоднозначно, нічого не пишеться.
    /// </summary>
    /// <returns>
    /// Зачеплені зв'язки й елементи: <c>true</c> — перепривʼязано, <c>false</c> — неоднозначно.
    /// Шлях AF регістронезалежний, тож і порівняння таке.
    /// </returns>
    private static (Dictionary<string, bool> Links, Dictionary<string, bool> Elements) MatchByPath(
        Dictionary<string, RegistrySyncSourceElement> elements,
        Dictionary<string, RegistrySyncLink> links,
        List<RegistrySyncRelink> relinks)
    {
        var takenLinks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var takenElements = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var candidates = elements.Values
            .Where(e => e.ExternalPath is not null && !links.ContainsKey(e.ExternalId))
            .ToLookup(e => e.ExternalPath!, StringComparer.OrdinalIgnoreCase);

        var lostByPath = links.Values
            .Where(l => l.ExternalPath is not null && !elements.ContainsKey(l.ExternalId))
            .GroupBy(l => l.ExternalPath!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var lost in lostByPath)
        {
            var found = candidates[lost.Key].OrderBy(e => e.ExternalId, StringComparer.Ordinal).ToList();
            if (found.Count == 0)
            {
                continue;
            }

            var gone = lost.OrderBy(l => l.ExternalId, StringComparer.Ordinal).ToList();
            var single = found.Count == 1 && gone.Count == 1;

            if (single)
            {
                relinks.Add(new RegistrySyncRelink(
                    gone[0].RegistryEntryId, gone[0].ExternalId, found[0].ExternalId, found[0].ExternalPath!));
            }

            gone.ForEach(l => takenLinks[l.ExternalId] = single);
            found.ForEach(e => takenElements[e.ExternalId] = single);
        }

        return (takenLinks, takenElements);
    }

    /// <summary>
    /// Новий запис <c>External</c> (<c>D-212</c> Q4): код — за <c>CodeMode</c>, назва — ім'я
    /// елемента, значення — активні мапінги, що привелися до типу.
    /// </summary>
    /// <returns><c>null</c> — елемент без імені: створювати нема з чого (лишається подія).</returns>
    private static RegistrySyncCreate? PlanCreate(
        RegistrySyncInput input,
        RegistrySyncSourceElement element,
        IReadOnlyList<RegistrySyncFieldMapping> mappings,
        List<RegistrySyncEvent> events)
    {
        if (string.IsNullOrWhiteSpace(element.Name))
        {
            return null;
        }

        var name = element.Name.Trim();
        var values = new List<RegistrySyncFieldValue>();

        foreach (var mapping in mappings)
        {
            // Вимкнений мапінг не пише й під час створення; атрибута немає — поле порожнє.
            if (!mapping.IsActive || !element.Attributes.TryGetValue(mapping.SourceAttribute, out var raw))
            {
                continue;
            }

            if (!TryIncoming(input, mapping, raw, out var typed, out var errorCode, out var messageKey))
            {
                // Запис створюється без цього поля: одне погане значення не має
                // лишати довідник без елемента, а відмова видна подією.
                events.Add(new RegistrySyncEvent(
                    RegistrySyncEventKind.ValueRejected, element.ExternalId, null, mapping.FieldCode,
                    SourceValue: raw, ErrorCode: errorCode, MessageKey: messageKey));
                continue;
            }

            if (typed is not null)
            {
                values.Add(new RegistrySyncFieldValue(mapping.RegistryFieldDefId, mapping.FieldCode, typed));
            }
        }

        var code = input.CodeMode == RegistryCodeMode.Auto ? null : name;
        return new RegistrySyncCreate(element.ExternalId, element.ExternalPath, code, name, values);
    }

    private static void PlanField(
        RegistrySyncInput input,
        RegistrySyncSourceElement element,
        RegistrySyncEntryState entry,
        RegistrySyncFieldMapping mapping,
        List<RegistrySyncUpdate> updates,
        List<RegistrySyncEvent> events)
    {
        var sourceKind = input.SourceKind;

        // Hybrid і Local: поле без активного мапінгу — локальне, синк про нього
        // не знає. External: поле лишається зовнішнім і з вимкненим мапінгом —
        // писати не можна, але розбіжність видно (нижче).
        if (!mapping.IsActive && sourceKind != RegistrySourceKind.External)
        {
            return;
        }

        // Атрибута немає у знімку — джерело про поле нічого не сказало. Це не
        // «порожньо»: стерти значення через те, що атрибут не прочитався,
        // означало б видати збій читання за дані.
        if (!element.Attributes.TryGetValue(mapping.SourceAttribute, out var raw))
        {
            return;
        }

        if (!TryIncoming(input, mapping, raw, out var incoming, out var errorCode, out var messageKey))
        {
            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.ValueRejected,
                element.ExternalId,
                entry.RegistryEntryId,
                mapping.FieldCode,
                SourceValue: raw,
                ErrorCode: errorCode,
                MessageKey: messageKey));
            return;
        }

        var current = entry.Values.TryGetValue(mapping.RegistryFieldDefId, out var known) ? known : null;
        var currentValue = current?.Value;

        // Ідемпотентність: те саме типізоване значення — нічого не пишеться і
        // нічого не повідомляється. decimal порівнюється за значенням: 1.0 == 1.00.
        if (Equals(currentValue, incoming))
        {
            return;
        }

        if (sourceKind == RegistrySourceKind.Local || !mapping.IsActive)
        {
            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.Diverged, element.ExternalId, entry.RegistryEntryId,
                mapping.FieldCode, currentValue, incoming));
            return;
        }

        // D-212 (1)/(2): людина виграє лише в Hybrid. External ручного запису не має
        // (D-211) — «людське» значення там є залишком, і синк його перезаписує.
        if (sourceKind == RegistrySourceKind.Hybrid && current is { LastWriterIsHuman: true })
        {
            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.ConflictKeptManual, element.ExternalId, entry.RegistryEntryId,
                mapping.FieldCode, currentValue, incoming));
            return;
        }

        updates.Add(new RegistrySyncUpdate(
            entry.RegistryEntryId, mapping.RegistryFieldDefId, mapping.FieldCode, currentValue, incoming));
    }

    /// <summary>
    /// Значення джерела → типізоване значення поля: код запису іншого довідника
    /// (<see cref="RegistrySyncFieldMapping.RefRegistryDefId"/>) розв'язується в
    /// <c>Id</c>, решта — <see cref="TryConvert"/>.
    /// </summary>
    private static bool TryIncoming(
        RegistrySyncInput input,
        RegistrySyncFieldMapping mapping,
        object? raw,
        out object? typed,
        out string? errorCode,
        out string? messageKey)
    {
        if (mapping.RefRegistryDefId is { } refDefId)
        {
            // Порожнє — джерело каже «порожньо»: поле очищується, як і для решти типів.
            if (CodeOf(raw) is not { } code)
            {
                typed = null;
                errorCode = messageKey = null;
                return true;
            }

            var codes = input.LookupCodes?.GetValueOrDefault(refDefId) ?? NoCodes;
            if (codes.TryGetValue(code, out var id))
            {
                typed = id;
                errorCode = messageKey = null;
                return true;
            }

            typed = null;
            errorCode = ValueRejectedCode;
            messageKey = EntryRefNotFoundKey;
            return false;
        }

        if (TryConvert(mapping, raw, out typed, out var rejection))
        {
            errorCode = messageKey = null;
            return true;
        }

        errorCode = rejection.ErrorCode;
        messageKey = rejection.Details?.GetValueOrDefault("messageKey") as string;
        return false;
    }

    /// <summary>Код запису зі значення атрибута: текст без пробілів по краях; порожнє — <c>null</c>.</summary>
    private static string? CodeOf(object? raw)
    {
        var text = raw switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString(),
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Приводить значення джерела до типу поля ТИМ САМИМ механізмом, що й ручний
    /// запис і імпорт: <see cref="CellValueReader.Normalize"/> +
    /// <see cref="RegistryValue.Set"/> (їх так само кличе
    /// <c>UpsertRegistryEntryHandler.ApplyValuesAsync</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Перевіряється лише ТИП (і одиниця для нечислових полів). Існування цілі
    /// <c>Lookup</c>, обов'язковість, ключі й правила довідника перевіряє
    /// <c>RegistryEntryWriter</c> під час запису — їм потрібна база.
    /// <para>
    /// ⚠ Проєкція типізованого значення нижче — шість рядків, структурно ті самі,
    /// що приватний <c>UpsertRegistryEntryHandler.RawValue</c>: той метод
    /// приватний, а файл — поза зоною цього кроку. Кандидат на винесення в
    /// <c>RegistryValue</c> у спільному кроці з <c>RegistryEntryWriter</c>.
    /// </para>
    /// </remarks>
    private static bool TryConvert(
        RegistrySyncFieldMapping mapping,
        object? raw,
        out object? typed,
        [NotNullWhen(false)] out DomainException? rejection)
    {
        var probe = new RegistryValue(registryEntryId: 0, mapping.RegistryFieldDefId);

        try
        {
            probe.Set(mapping.DataType, CellValueReader.Normalize(raw), mapping.UnitId);
        }
        catch (DomainException ex)
        {
            typed = null;
            rejection = ex;
            return false;
        }

        typed = mapping.DataType switch
        {
            CellDataType.String => probe.ValueString,
            CellDataType.Int or CellDataType.Decimal => probe.ValueNumeric,
            CellDataType.Bool => probe.ValueBool,
            CellDataType.Date => probe.ValueDate,
            CellDataType.Lookup => probe.ValueRefEntryId,
            CellDataType.Unit => probe.ValueUnitId,
            _ => null,
        };
        rejection = null;
        return true;
    }

    private static Dictionary<string, T> ToUniqueMap<T>(
        IEnumerable<T> items, Func<T, string> key, string parameter)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!map.TryAdd(key(item), item))
            {
                throw new ArgumentException($"Дубль зовнішнього ідентифікатора «{key(item)}».", parameter);
            }
        }

        return map;
    }
}
