// src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Записи довідника **станом на дату періоду**, а не «активні зараз»
/// (ФВ-8.5).
/// </summary>
/// <remarks>
/// Різниця принципова: документ за березень має бачити дозволи, чинні в
/// березні, навіть якщо сьогодні жовтень і половина з них уже недійсна.
/// </remarks>
public sealed class GetRegistryEntriesHandler(
    IRegistryStore registries,
    RegistryResolver resolver,
    IRegistryEntryCache cache,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IDocumentStore? documents = null,
    IPeriodStore? periods = null,
    IPermitGroupStore? groupStore = null,
    Permits.Groups.IPermitGroupResolver? groups = null,
    IPermitTypeStore? permitTypes = null)
{
    // ⚠ `documents` і `periods` потрібні лише режиму «вікно документа» (`documentId`); `groupStore`, `groups` і `permitTypes` -
    // піклеру ГРУПИ типів дозволів (PRMG). Необов'язкові для тестів, що будують обробник руками, - контейнер підставляє їх завжди.

    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає записи довідника на дату.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="asOf">Дата періоду.</param>
    /// <param name="parentEntryId">Обраний батьківський запис для каскаду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    public Task<IReadOnlyList<RegistryEntryDto>> HandleAsync(
        string registryCode, DateOnly asOf, long? parentEntryId, CancellationToken ct)
        => HandleAsync(registryCode, asOf, parentEntryId, documentId: null, ct);

    /// <summary>Читає записи довідника на дату або у ВІКНІ проєкту документа.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="asOf">Дата періоду; ігнорується, коли задано <paramref name="documentId"/>.</param>
    /// <param name="parentEntryId">Обраний батьківський запис для каскаду.</param>
    /// <param name="documentId">
    /// Документ, у шапку якого обирається запис. Задано - перелік звужується до записів, придатних у вікні проєкту
    /// документа <c>[PeriodStart, PeriodEnd]</c> (активні, не видалені, чинні бодай один день вікна): те саме правило,
    /// що в <c>PATCH …/header</c> (<c>ECR-HDR-4223</c>). <c>asOf</c> тоді не потрібен. Невидимий документ - 404.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника чи документа немає - <c>ECR-REG-0404</c> / <c>ECR-DOC-0404</c>.</exception>
    public async Task<IReadOnlyList<RegistryEntryDto>> HandleAsync(
        string registryCode, DateOnly asOf, long? parentEntryId, long? documentId, CancellationToken ct)
    {
        // ⚠ Глобальне право АБО ресурсний грант рівня Read на ЦЕЙ довідник
        // (A7-58). Всередині `RegistryAccess.RequireAsync` довідник з коду
        // (`RegistryLookup`) резолвиться ЛИШЕ тоді, коли глобального
        // Registry.View нема або в профілі є заборони на довідники (S18) —
        // власник глобального права без заборон не платить зайвим
        // FindDefinitionAsync ТУТ.
        //
        // ⚠ Явний виклик нижче — ОКРЕМИЙ похід у базу, потрібен незалежно від
        // результату перевірки права: гейт asOf-обов'язковості за
        // `definition.IsTemporal` (коментар нижче) фізично вимагає визначення
        // ДО перевірки asOf, тобто для КОЖНОГО користувача, а не лише для
        // того, хто йде через ресурсний грант. `RegistryEntriesAsOfValidationTests`
        // більше не тримає зворотної інваріанти («похід у базу не випереджає
        // asOf») — вона й була джерелом дефекту: Lookup нетемпорального
        // довідника не працював НІКОЛИ, бо перевірка asOf ішла раніше, ніж
        // хтось встигав дізнатися, що довідник нетемпоральний.
        // ✎ Рішення людини 26.10 (P3): пікер документа (`documentId`) приймає й Registry.View ролі, призначеної на ПРОЄКТ
        // документа (`ScopedProjectAccess.RegistryView`) - узгоджено з PATCH шапки/комірки. Без `documentId` - як було.
        // Запит проєкту і перевірка йдуть лише після відмови глобальної перевірки: звичайні користувачі нічого не платять.
        Security.AccessProfile? viewer = null;
        int? viewerProject = null;
        var viaScoped = false;
        try
        {
            viewer = await RegistryAccess
                .RequireAsync(access, currentUser, Permission, GrantLevel.Read, new RegistryLookup(registries, registryCode), ct)
                .ConfigureAwait(false);
        }
        catch (AccessDeniedException) when (documentId is not null)
        {
            (viewer, viewerProject) = await ScopedViewerAsync(documentId.Value, ct).ConfigureAwait(false);
            if (viewer is null)
            {
                throw;
            }

            viaScoped = true;
        }

        // P52 (Аудит-2 W14h): запит проєкту документа для профілю зі scoped/narrowed Deny виконується ДО рішення про
        // існування реєстру - Deny-реєстр і неіснуючий мають однаковий шлях і кількість запитів (без часового оракула).
        int? denyProject = null;
        if (!viaScoped && documentId is not null && Permits.Groups.LookupTargetMatcher.ViewProjectNeeded(viewer))
        {
            denyProject = await access.DocumentProjectIdAsync(documentId.Value, ct).ConfigureAwait(false);
        }

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{registryCode}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = registryCode });

        // P50 (Аудит-2 W14g): Deny, виданий ролі з областю, діє в документах її проєкту - 404, як неіснуючий (S18).
        // Scoped-шлях: View/грант проєкту + Deny перевіряє MayChoose; глобальний користувач зі scoped-Deny - лише коли такі є.
        if (viaScoped)
        {
            // P52: профіль з областю без View (інший проєкт, звужена роль) чи під Deny - 404, як неіснуючий реєстр
            // (раніше існуючий давав 403, неіснуючий 404 - витік існування коду). Глобальний користувач без областей - 403.
            if (!Permits.Groups.LookupTargetMatcher.MayChoose(viewer, definition.Id, viewerProject))
            {
                throw RegistryAccess.NotFound(registryCode);
            }
        }
        else if (denyProject is not null || (documentId is not null && Permits.Groups.LookupTargetMatcher.ViewProjectNeeded(viewer)))
        {
            if (RegistryAccess.IsDeniedIn(viewer!, definition.Id, denyProject))
            {
                throw RegistryAccess.NotFound(registryCode);
            }
        }

        // ⛔ Відсутній або зіпсований `asOf` — ВІДМОВА, а не `0001-01-01`
        // (аудит 2026-09-16, §9), АЛЕ лише для ТЕМПОРАЛЬНОГО довідника
        // (`RegistryDef.IsTemporal`). Довідник без вікна чинності (`ValidFrom`/
        // `ValidTo` завжди `null`, `ValidityWindow.Contains` тоді true для
        // будь-якої дати) не має «дати періоду», на яку залежав би перелік
        // записів — вимагати asOf для нього означало б вимагати параметр, який
        // нічого не змінює. До цього фіксу перевірка йшла БЕЗУМОВНО, ДО фетчу
        // визначення, і Lookup-піцкер нетемпорального довідника (наприклад,
        // Substance) був непрацездатним завжди: жоден клієнтський виклик
        // `GET …/entries` не передавав asOf, і кожен такий запит падав
        // `422 ECR-REQ-0422`, незалежно від прапорця.
        //
        // Для ТЕМПОРАЛЬНОГО довідника застереження лишається чинним: `[FromQuery]
        // DateOnly` без значення резолвиться у `default` — дату, на яку жоден
        // темпоральний запис не чинний, — і мовчазна підстановка збрехала б
        // про «довідник порожній» там, де насправді забули дату періоду.
        // ⛔ RT-11 (борг RT-12, D-155): частину композиції видно рівно тоді, коли видно батька —
        // рекурсивно вгору. Тому ланцюжок батьківських довідників читається ДО ключа кешу: їхні
        // ревізії входять у ключ, інакше видалення кейсу не сховало б його склад у пікері, доки
        // не зміниться сам довідник складу.
        var chain = await CompositionChainAsync(definition, ct).ConfigureAwait(false);

        // Режим «вікно документа»: межі проєкту беруться з документа на сервері, а не від клієнта.
        var window = documentId is { } docId ? await ResolveWindowAsync(docId, ct).ConfigureAwait(false) : null;

        // PRMG: піклер групи типів дозволів - обʼєднання дозволів типів складу, а не записи самого (порожнього) реєстру групи.
        if (Permits.Groups.PermitGroupRegistryGuard.IsGroupRegistry(definition.Code) && groups is not null && groupStore is not null)
        {
            return await GroupEntriesAsync(definition, asOf, window, ct).ConfigureAwait(false);
        }

        // ⚠ Дата потрібна й тоді, коли темпоральний не сам довідник, а його батько композиції:
        // перелік частин залежить від того, чи чинний батько на дату періоду.
        if (window is null && (definition.IsTemporal || chain.Exists(link => link.Parent.IsTemporal)) && asOf == default)
        {
            throw new BusinessRuleException(
                Domain.Errors.ErrorCodes.RequestInvalid,
                "Параметр asOf обов'язковий: довідник темпоральний, і перелік записів "
                + "залежить від дати періоду, а не від «сьогодні».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.asOfRequired",
                    ["parameter"] = "asOf",
                });
        }

        // ⚠ DataRevision у ключі, а не час життя: та сама схема, що з
        // метаданими (D-16). Запис довідника змінили — ключ інший, старе
        // значення нікому не заважає і не потребує інвалідації між інстансами.
        //
        // asOf теж у ключі: той самий довідник на різні дати — різні списки, і
        // спільний запис віддавав би березневий перелік у жовтневому документі.
        var parents = string.Concat(chain.Select(link =>
            $":c{link.Parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"r{link.Parent.DataRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var when = window is { } w0 ? $"w{w0.Start:yyyy-MM-dd}_{w0.End:yyyy-MM-dd}" : $"{asOf:yyyy-MM-dd}";
        var key = $"reg:{definition.Code}:r{definition.DataRevision}{parents}:{when}:p{parentEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";

        var selected = await cache.GetOrAddAsync(
            key,
            async token =>
            {
                var entries = await registries.ListEntriesAsync(definition.Id, token).ConfigureAwait(false);

                // Зв'язки читаються лише коли є що звужувати: без каскаду це
                // зайвий запит на кожне відкриття випадного списку.
                IReadOnlyList<RegistryEntryLink> links = parentEntryId is null
                    ? []
                    : await registries.ListInboundLinksAsync(definition.Id, token).ConfigureAwait(false);

                var own = window is { } w
                    ? resolver.SelectInWindow(entries, links, w.Start, w.End, parentEntryId)
                    : resolver.Select(entries, links, asOf, parentEntryId);
                if (chain.Count == 0 || own.Count == 0)
                {
                    return own;
                }

                var visible = await CompositionVisibilityAsync(entries, chain, window?.End ?? asOf, token).ConfigureAwait(false);
                return own.Where(e => visible(e.Id)).ToList();
            },
            ct).ConfigureAwait(false);

        // Id і Display: у комірці зберігається Id (ФВ-8.8), Display лише
        // показується. Тому перейменування не змінює історичних даних.
        return selected
            .Select(e => new RegistryEntryDto(
                e.Id,
                e.Code,
                e.DisplayL10n.Get(currentUser.Language) ?? e.Code,
                e.ParentEntryId,
                e.ValidFrom,
                e.ValidTo))
            .ToList();
    }

    /// <summary>
    /// Піклер ГРУПИ типів дозволів: активні й чинні дозволи (станом на <paramref name="asOf"/> або у вікні проєкту документа)
    /// типів складу, до реєстрів яких у читача немає заборони (S18). Підпис «номер — тип»: <c>Display</c> лишається номером,
    /// тип - у <c>TypeCode</c>/<c>TypeName</c>. Порядок - за складом групи, далі за номером.
    /// </summary>
    /// <remarks>
    /// ⛔ Ключ кешу несе відфільтрований набір реєстрів-членів із їхніми <c>DataRevision</c>: читачі з різними забороною бачать різні
    /// набори, тож спільний ключ віддав би чужий склад. Записи читаються ОДНИМ запитом <c>IN (члени)</c>. Деактивована група віддає
    /// порожній перелік. Група з дозволом на реєстр групи, але без глобального права, бачить усіх не заборонених членів.
    /// </remarks>
    private async Task<IReadOnlyList<RegistryEntryDto>> GroupEntriesAsync(
        RegistryDef group, DateOnly asOf, (DateOnly Start, DateOnly End)? window, CancellationToken ct)
    {
        if (window is null && asOf == default)
        {
            throw new BusinessRuleException(
                Domain.Errors.ErrorCodes.RequestInvalid,
                "Параметр asOf обов'язковий: перелік дозволів групи залежить від дати періоду, а не від «сьогодні».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.asOfRequired",
                    ["parameter"] = "asOf",
                });
        }

        if (!group.IsActive)
        {
            return [];
        }

        await groups!.EnsureFreshAsync(ct).ConfigureAwait(false);
        var profile = await access.BuildProfileAsync(currentUser.UserId!.Value, ct).ConfigureAwait(false);

        var members = groups.MemberRegistryIds(group.Id).Where(id => !RegistryAccess.IsDenied(profile, id)).ToList();
        if (members.Count == 0)
        {
            return [];
        }

        var revisions = await groupStore!.GetDataRevisionsAsync(members, ct).ConfigureAwait(false);
        var when = window is { } w ? $"w{w.Start:yyyy-MM-dd}_{w.End:yyyy-MM-dd}" : $"{asOf:yyyy-MM-dd}";
        var key = $"reg:{group.Code}:r{group.DataRevision}:m"
            + string.Join(',', members.Select(m =>
                $"{m.ToString(System.Globalization.CultureInfo.InvariantCulture)}.{revisions.GetValueOrDefault(m).ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
            + $":{when}";

        var selected = await cache.GetOrAddAsync(
            key,
            async token =>
            {
                var entries = window is { } span
                    ? await groupStore.ListMemberEntriesAsync(members, span.Start, span.End, token).ConfigureAwait(false)
                    : await groupStore.ListMemberEntriesAsync(members, asOf, asOf, token).ConfigureAwait(false);
                var chosen = window is { } win
                    ? resolver.SelectInWindow(entries, [], win.Start, win.End, null)
                    : resolver.Select(entries, [], asOf, null);

                // Порядок складу групи, далі номер; інакше запис першого типу тонув би серед решти за Ordinal=0.
                var order = members.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
                return (IReadOnlyList<RegistryEntry>)[.. chosen
                    .OrderBy(e => order.GetValueOrDefault(e.RegistryDefId, int.MaxValue))
                    .ThenBy(e => e.DisplayL10n.Get("en") ?? e.Code, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Id)];
            },
            ct).ConfigureAwait(false);

        var types = permitTypes is null
            ? new Dictionary<int, PermitTypeSummary>()
            : (await permitTypes.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.HeaderRegistryDefId);

        return [.. selected.Select(e =>
        {
            var type = types.GetValueOrDefault(e.RegistryDefId);
            return new RegistryEntryDto(
                e.Id,
                e.Code,
                e.DisplayL10n.Get(currentUser.Language) ?? e.Code,
                null,
                e.ValidFrom,
                e.ValidTo,
                type?.Code,
                type?.NameL10n.Get(currentUser.Language));
        })];
    }

    /// <summary>
    /// Профіль і проєкт документа, коли профіль має ролі з областю (їхній View/гранти/Deny рахує <c>MayChoose</c>); інакше <c>null</c>.
    /// </summary>
    private async Task<(Security.AccessProfile? Profile, int? ProjectId)> ScopedViewerAsync(long documentId, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return (null, null);
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (profile.Scoped.Count == 0 || profile.IsIntegrationWriter)
        {
            return (null, null);
        }

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false);
        return (profile, projectId);
    }

    /// <summary>Вікно проєкту документа; невидимий чи відсутній документ - 404 (як <c>GET …/header</c>, B-08).</summary>
    private async Task<(DateOnly Start, DateOnly End)?> ResolveWindowAsync(long documentId, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var profile = userId is { } id ? await access.BuildProfileAsync(id, ct).ConfigureAwait(false) : null;
        var document = documents is not null && profile is not null
            ? await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false)
            : null;
        var project = document is not null && periods is not null && profile!.SeesDocumentsOf(document.ProjectId)
            ? await periods.FindProjectAsync(document.ProjectId, ct).ConfigureAwait(false)
            : null;

        return project is null
            ? throw new NotFoundException(
                Domain.Errors.ErrorCodes.DocumentNotFound,
                $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
            : (project.PeriodStart, project.PeriodEnd);
    }

    /// <summary>
    /// Ланцюжок композиції вгору: довідник-дитина → поле композиції → довідник-батько → … (<c>D-155</c>).
    /// Порожній — довідник не є частиною іншого.
    /// </summary>
    /// <remarks>
    /// ⚠ Цикл композицій забороняє опис (<c>RegistryCompositionRules</c>), але дані могли прийти
    /// повз нього — обхід зупиняється на вже баченому довіднику, а не зациклюється.
    /// </remarks>
    private async Task<List<CompositionLink>> CompositionChainAsync(RegistryDef definition, CancellationToken ct)
    {
        var chain = new List<CompositionLink>();
        var seen = new HashSet<int> { definition.Id };
        var child = definition;

        while (child.Fields.FirstOrDefault(f => f.RelationKind == RegistryRelationKind.Composition
                                                && f.RefRegistryDefId is not null) is { } field
               && seen.Add(field.RefRegistryDefId!.Value)
               && await registries.FindDefinitionByIdAsync(field.RefRegistryDefId.Value, ct).ConfigureAwait(false) is { } parent)
        {
            chain.Add(new CompositionLink(child, field, parent));
            child = parent;
        }

        return chain;
    }

    /// <summary>
    /// Предикат «запис видно разом із батьками композиції» — одне правило на всіх споживачів
    /// (<see cref="RegistryResolver.VisibleWithCompositionParents"/>, той самий, що в знімку для формул).
    /// </summary>
    private async Task<Func<long, bool>> CompositionVisibilityAsync(
        IReadOnlyList<RegistryEntry> entries,
        IReadOnlyList<CompositionLink> chain,
        DateOnly asOf,
        CancellationToken ct)
    {
        var byId = new Dictionary<long, RegistryEntry>();
        var registryOf = new Dictionary<long, int>();
        var parentOf = new Dictionary<long, long?>();
        var children = chain.Select(link => link.Child.Id).ToHashSet();

        void Remember(IEnumerable<RegistryEntry> set)
        {
            foreach (var entry in set)
            {
                byId[entry.Id] = entry;
                registryOf[entry.Id] = entry.RegistryDefId;
            }
        }

        Remember(entries);
        IReadOnlyList<RegistryEntry> level = entries;

        foreach (var link in chain)
        {
            // Значення поля композиції кожного запису рівня — його батько.
            var values = await registries
                .ListValuesForEntriesAsync([.. level.Select(e => e.Id)], ct)
                .ConfigureAwait(false);
            foreach (var value in values.Where(v => v.RegistryFieldDefId == link.Field.Id))
            {
                parentOf[value.RegistryEntryId] = value.ValueRefEntryId;
            }

            level = await registries.ListEntriesAsync(link.Parent.Id, ct).ConfigureAwait(false);
            Remember(level);
        }

        return resolver.VisibleWithCompositionParents(
            id => byId.TryGetValue(id, out var entry) && resolver.IsSelectable(entry, asOf),
            id => registryOf.TryGetValue(id, out var registry) && children.Contains(registry)
                ? (true, parentOf.GetValueOrDefault(id))
                : (false, null));
    }

    /// <summary>Ланка композиції: довідник-дитина, його поле композиції і довідник-батько.</summary>
    private sealed record CompositionLink(RegistryDef Child, RegistryFieldDef Field, RegistryDef Parent);
}
