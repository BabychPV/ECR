// src/Ecr.Application/Registries/Rows/RegistryBatchHandler.cs
using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Keys;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Rows;

/// <summary>
/// Пакетний запис рядків довідника з <c>dryRun</c> (RT-14, FEATURE-REGISTRY-TABLES §4.6, §7.1):
/// сітка редактора зберігає всі брудні рядки одним пакетом. Право <c>Registry.EditData</c> або
/// грант <c>Write</c> на довідник.
/// </summary>
/// <remarks>
/// ⛔ Пакет — ОДНА транзакція: видалення (<see cref="DeleteRegistryEntryHandler"/>) і записи
/// (<see cref="RegistryEntryWriter"/>, з ключами) відбуваються в ній насправді, а за хоч однієї
/// помилки рядка або <c>dryRun</c> транзакція відкочується. Так прогін без запису бачить рівно те,
/// що побачив би запис: ключ, звільнений видаленням у тому самому пакеті, обмін ключами між
/// записами пакета, конфлікт із записом поза пакетом під блокуванням.
/// <para>
/// ⚠ Номер послідовності авто-коду транзакція не повертає, тому <c>dryRun</c> бере в writer'а
/// заглушкові коди (<see cref="RegistryEntryWriteBatch.PlaceholderAutoCodes"/>).
/// </para>
/// <para>
/// ⚠ <c>baseVersion</c> (<c>D-166</c>) звіряється з <c>PeriodStart</c> до запису, в тій самій
/// транзакції, але без блокування рядків: запис іншого, що закомітився між звіркою й записом,
/// не помітить (борг названо в описі кроку).
/// </para>
/// </remarks>
public sealed partial class RegistryBatchHandler(
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IUnitOfWork uow,
    RegistryEntryWriter writer,
    DeleteRegistryEntryHandler deleter,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    Rules.IRegistryRuleEngine rules)
{
    /// <summary>Право на зміну даних довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Найбільше рядків у пакеті (§7.1).</summary>
    public const int MaxItems = 2000;

    /// <summary>Записів на одне читання версій — стеля <see cref="IRegistryRowsQuery.ReadRowsAsync"/>.</summary>
    private const int VersionChunk = 500;

    /// <summary>Записує (або лише перевіряє) пакет.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="request">Пакет.</param>
    /// <param name="dryRun">Прогін без запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-REG-0404</c>: довідника немає.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0422</c>: пакет завеликий чи рядок неправильний.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-REG-4092</c>: гонка за ключем під час запису.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-REG-4221</c> (RT-17a): без <c>dryRun</c> порушено правило довідника рівня <c>Error</c> —
    /// власне правило рядка чи правило батька композиції; не записано нічого.
    /// </exception>
    public async Task<RegistryBatchResult> HandleAsync(
        string registryCode, RegistryBatchRequest request, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = request.Items ?? [];

        await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Write, new RegistryLookup(registries, registryCode), ct)
            .ConfigureAwait(false);

        Validate(items);

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.RegistryEntryNotFound,
                $"Довідника «{registryCode}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = registryCode });

        // ⛔ D-211: ДО dryRun — пробний прогін External-довідника теж відмова, а не «усе пройде».
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);

        var states = items.Select(i => new RowState(i)).ToList();
        await ResolveAsync(definition, states, ct).ConfigureAwait(false);
        ParseNumbers(definition, states);
        await CheckVersionsAsync(states, ct).ConfigureAwait(false);

        IReadOnlyList<RegistryEntryWriteRow> written = [];
        var ruleCheck = Rules.RegistryRuleCheck.None;
        try
        {
            await uow.ExecuteInTransactionAsync(
                async token =>
                {
                    await DeleteAsync(registryCode, states, token).ConfigureAwait(false);
                    await CheckKeysAsync(definition, states, token).ConfigureAwait(false);
                    written = await WriteAsync(definition, states, dryRun, token).ConfigureAwait(false);

                    // ⛔ RT-17a (§6, ⚠ «Агрегатні правила й поштучний ввід»): правила — на стані ПІСЛЯ
                    // всього пакета, у тій самій транзакції, для записаних рядків і батьків композиції
                    // записаних та видалених. Error без dryRun — 422 на весь пакет (транзакція
                    // відкочується); у dryRun — перелік у звіті, щоб сітка показала Σ до збереження.
                    if (states.TrueForAll(s => s.Errors.Count == 0))
                    {
                        ruleCheck = await rules.EvaluateAsync(
                            definition,
                            [.. written.Where(w => w.IsNew || w.IsChanged).Select(w => w.Entry.Id)],
                            [.. states.Where(s => s.Deleted).Select(s => s.Entry!.Id)],
                            businessDate: null,
                            token).ConfigureAwait(false);

                        if (!dryRun)
                        {
                            ruleCheck.ThrowIfErrors();
                        }
                    }

                    if (dryRun || states.Exists(s => s.Errors.Count > 0))
                    {
                        throw new RollbackBatch();
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (RollbackBatch)
        {
            // Відкат — задуманий кінець прогону: dryRun або помилки рядків.
        }

        var applied = !dryRun && states.TrueForAll(s => s.Errors.Count == 0);
        var report = await ReportAsync(states, written, applied, dryRun, ct).ConfigureAwait(false);
        return report with { Rules = ruleCheck.Violations };
    }

    /// <summary>Межі й форма самого запиту — 422 на весь пакет, до будь-якого читання даних.</summary>
    private static void Validate(IReadOnlyList<RegistryBatchItemDto> items)
    {
        if (items.Count > MaxItems)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"У пакеті {items.Count} рядків, а найбільше — {MaxItems}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.batchTooLarge",
                    ["max"] = MaxItems.ToString(CultureInfo.InvariantCulture),
                    ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        var ids = new HashSet<long>();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var op = item?.Op;
            var valid = item is not null
                && (op is "upsert" || (op is "delete" && item.Id is not null))
                && (item.Id is not { } id || ids.Add(id))
                && (item.Id is not null || string.IsNullOrWhiteSpace(item.Code) || codes.Add(item.Code.Trim()));

            if (!valid)
            {
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Рядок пакета «{item?.ClientRowId}» неправильний: дія, Id або повтор.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REQ-0422.batchItemInvalid",
                        ["clientRowId"] = item?.ClientRowId,
                    });
            }
        }
    }

    /// <summary>Наявні записи рядків і коди нових — помилки рядків, а не відмова пакета.</summary>
    private async Task ResolveAsync(RegistryDef definition, List<RowState> states, CancellationToken ct)
    {
        var entries = await rows.ListEntriesAsync(definition.Id, asOfUtc: null, ct).ConfigureAwait(false);
        var byId = entries.ToDictionary(e => e.Id);
        var byCode = entries.Where(e => !e.IsDeleted)
            .GroupBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var state in states)
        {
            if (state.Item.Id is { } id)
            {
                // Запис іншого довідника для цього пакета не існує — так само, як у DELETE (ФВ-14.9a).
                if (byId.TryGetValue(id, out var entry) && !entry.IsDeleted)
                {
                    state.Entry = entry;
                    state.Code = entry.Code;
                }
                else
                {
                    state.Fail(null, ErrorCodes.RegistryEntryNotFound, "err.ECR-REG-0404.registryEntry", ("entryId", Invariant(id)));
                }

                continue;
            }

            state.Code = state.Item.Code?.Trim() ?? string.Empty;
            if (state.Code.Length == 0 && definition.CodeMode != RegistryCodeMode.Auto)
            {
                state.Fail("code", "ECR-CFG-0422", "err.ECR-CFG-0422.invalidCode", ("code", state.Code));
            }
            else if (state.Code.Length > 0 && byCode.TryGetValue(state.Code, out var taken))
            {
                // ⚠ Новий рядок із кодом наявного запису — відмова, а не «оновити знайдений»
                // (та сама причина, що в UpsertRegistryEntryHandler.CreateAsync).
                state.Fail(null, ErrorCodes.RegistryEntryInUse, "err.ECR-REG-0409.entryCodeTaken", ("code", state.Code), ("id", Invariant(taken.Id)));
            }
        }
    }

    /// <summary>
    /// Числа, набрані текстом, — за мовою користувача (<see cref="RegistryUserNumbers"/>), ДО звірки
    /// ключів і writer'а: і <c>CheckKeysAsync</c>, і <c>RegistryBatchKeys</c> у writer'і бачать те саме
    /// число, тож «12,5» (ru) і <c>12.5</c> (JSON) — дубль ключа, а не два записи.
    /// </summary>
    /// <remarks>Нечислове чи неоднозначне — помилка РЯДКА (звіт 200, <c>applied=false</c>), як решта RT-14.</remarks>
    private void ParseNumbers(RegistryDef definition, List<RowState> states)
    {
        var culture = Localization.NumberCulture.ForLanguage(currentUser.Language);
        foreach (var state in states.Where(s => s.Item.Op == "upsert" && s.Errors.Count == 0))
        {
            if (RegistryUserNumbers.TryParse(definition, state.Item.Values, culture, out var error) is { } parsed)
            {
                state.Values = parsed;
                continue;
            }

            state.Fail(
                error!.FieldCode, "ECR-REG-0422", error.MessageKey,
                [.. error.Details()
                    .Where(d => d.Key != "messageKey")
                    .Select(d => (d.Key, d.Value as string))]);
        }
    }

    /// <summary>Застарілий <c>baseVersion</c> — помилка рядка <c>entryChanged</c> (<c>D-166</c>).</summary>
    private async Task CheckVersionsAsync(List<RowState> states, CancellationToken ct)
    {
        var checkedRows = states.Where(s => s.Entry is not null && s.Item.BaseVersion is not null).ToList();
        foreach (var chunk in checkedRows.Chunk(VersionChunk))
        {
            var slice = await rows.ReadRowsAsync([.. chunk.Select(s => s.Entry!.Id)], asOfUtc: null, ct).ConfigureAwait(false);
            foreach (var state in chunk)
            {
                var entry = state.Entry!;

                // Нерозбірна версія — теж «не та»: клієнт міг прислати лише те, що віддав сервер.
                if (!RegistryRowVersion.TryDecode(state.Item.BaseVersion, out var baseVersion)
                    || slice.Versions.GetValueOrDefault(entry.Id) != baseVersion)
                {
                    state.Fail(
                        null, ErrorCodes.RegistryEntryChanged, "err.ECR-REG-4093.entryChanged",
                        ("entryId", Invariant(entry.Id)), ("entryCode", entry.Code));
                }
            }
        }
    }

    /// <summary>Видалення — наявним обробником (посилання, каскад композиції, ключі, журнал).</summary>
    private async Task DeleteAsync(string registryCode, List<RowState> states, CancellationToken ct)
    {
        foreach (var state in states.Where(s => s.Item.Op == "delete" && s.Errors.Count == 0))
        {
            try
            {
                await deleter.HandleAsync(registryCode, state.Entry!.Id, ct).ConfigureAwait(false);
                state.Deleted = true;
            }
            catch (EcrException ex) when (ex is BusinessRuleException or NotFoundException)
            {
                state.Fail(null, ex.ErrorCode, MessageKey(ex.Details), [.. Params(ex.Details)]);
            }
        }
    }

    /// <summary>
    /// Ключ, який тримає запис поза пакетом, — помилка рядка <c>keyTaken</c> (§7.1), а не 409 на весь
    /// пакет. Тримачі з пакета (записувані й щойно видалені) не рахуються: їхні ключі пакет переписує.
    /// </summary>
    /// <remarks>
    /// ⚠ Перевірка для звіту, без блокування. Авторитетна — під <c>UPDLOCK, HOLDLOCK</c> у службі
    /// ключів під час запису; те, що вона ловить після цієї, — гонка (409).
    /// </remarks>
    private async Task CheckKeysAsync(RegistryDef definition, List<RowState> states, CancellationToken ct)
    {
        var keys = writer.Keys;
        var upserts = states.Where(s => s.Item.Op == "upsert" && s.Errors.Count == 0).ToList();
        if (keys is null || upserts.Count == 0)
        {
            return;
        }

        var keyDefs = await keys.ListActiveKeysAsync(definition.Id, ct).ConfigureAwait(false);
        if (keyDefs.Count == 0)
        {
            return;
        }

        var inBatch = states.Where(s => s.Deleted || upserts.Contains(s)).Select(s => s.Entry?.Id).OfType<long>().ToHashSet();
        var stored = (await registries.ListValuesForEntriesAsync([.. upserts.Select(s => s.Entry?.Id).OfType<long>()], ct).ConfigureAwait(false))
            .ToLookup(v => v.RegistryEntryId);

        var effective = new List<(RowState State, Dictionary<int, RegistryValue> Values)>();
        foreach (var state in upserts)
        {
            try
            {
                var existing = state.Entry is null ? [] : stored[state.Entry.Id].ToList();
                effective.Add((state, RegistryBatchKeys.EffectiveValues(definition, existing, state.Values)));
            }
            catch (Ecr.Domain.Abstractions.DomainException)
            {
                // Значення не того типу — помилку рядка дасть writer; ключ такого рядка не рахується.
            }
        }

        foreach (var key in keyDefs)
        {
            var hashed = effective
                .Select(e => (e.State, Hash: RegistryKeyService.HashOf(definition, key, e.Values)))
                .Where(e => e.Hash is not null)
                .ToList();
            var holders = (await keys.FindHoldersAsync(key.Id, [.. hashed.Select(h => h.Hash!)], ct).ConfigureAwait(false))
                .ToLookup(h => Convert.ToHexString(h.KeyHash!));

            foreach (var (state, hash) in hashed.Where(h => h.State.Errors.Count == 0))
            {
                var window = state.Entry?.Window ?? new ValidityWindow(null, null);
                var conflict = holders[Convert.ToHexString(hash!)]
                    .Where(h => !inBatch.Contains(h.EntryId))
                    .FirstOrDefault(h => !definition.IsTemporal || RegistryKeyService.Overlaps(h.Window, window));

                if (conflict is not null)
                {
                    state.Fail(
                        null, ErrorCodes.RegistryKeyConflict,
                        definition.IsTemporal ? "err.ECR-REG-4092.keyWindowOverlap" : "err.ECR-REG-4092.keyTaken",
                        ("key", key.Code), ("keyText", conflict.KeyText), ("entryId", Invariant(conflict.EntryId)), ("entryCode", conflict.EntryCode));
                }
            }
        }
    }

    /// <summary>Записи — одним викликом спільного writer'а (значення, ключі пакета, ревізія, аудит).</summary>
    private async Task<IReadOnlyList<RegistryEntryWriteRow>> WriteAsync(
        RegistryDef definition, List<RowState> states, bool dryRun, CancellationToken ct)
    {
        var upserts = states.Where(s => s.Item.Op == "upsert" && s.Errors.Count == 0).ToList();
        if (upserts.Count == 0)
        {
            return [];
        }

        var batch = new RegistryEntryWriteBatch(
            definition.Id,
            [.. upserts.Select(s => new RegistryEntryWrite(s.Code, s.Values))])
        {
            PlaceholderAutoCodes = dryRun,
        };

        RegistryEntryWriteResult result;
        try
        {
            result = await writer.WriteAsync(batch, ct).ConfigureAwait(false);
        }
        catch (BusinessRuleException ex) when (ex.ErrorCode == ErrorCodes.RegistryKeyConflict && states.Exists(s => s.Errors.Count > 0))
        {
            // Рядок, відхилений раніше, лишив свій ключ у базі, і рядок, що мав його зайняти, упав на
            // ньому. Пакет однаково відкочується; причину вже названо в рядку, що відхилений.
            return [];
        }

        foreach (var error in result.Errors)
        {
            var state = upserts[error.Row - 1];
            var code = CodeOf(error.MessageKey);
            state.Fail(error.Field, code, error.MessageKey, ("fields", error.Field), ("code", error.Key));
        }

        return [.. result.Rows.Select(r => r with { Row = states.IndexOf(upserts[r.Row - 1]) })];
    }

    /// <summary>Звіт: рядки в порядку пакета, версії — після запису.</summary>
    private async Task<RegistryBatchResult> ReportAsync(
        List<RowState> states, IReadOnlyList<RegistryEntryWriteRow> written, bool applied, bool dryRun, CancellationToken ct)
    {
        var byIndex = written.ToDictionary(w => w.Row);
        var versions = new Dictionary<long, DateTime>();
        if (applied)
        {
            var ids = written.Select(w => w.Entry.Id).ToList();
            foreach (var chunk in ids.Chunk(VersionChunk))
            {
                foreach (var (id, version) in (await rows.ReadRowsAsync(chunk, asOfUtc: null, ct).ConfigureAwait(false)).Versions)
                {
                    versions[id] = version;
                }
            }
        }

        var result = new List<RegistryBatchRowResult>(states.Count);
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            byIndex.TryGetValue(i, out var row);
            var status = state.Errors.Count > 0 ? "error"
                : state.Item.Op == "delete" ? "deleted"
                : row is null ? "unchanged"
                : row.IsNew ? "added"
                : row.IsChanged ? "updated" : "unchanged";

            // Id нового запису після відкату вигаданий — його не віддаємо.
            var entryId = row is { IsNew: true } ? (applied ? row.Entry.Id : null) : state.Entry?.Id;
            var version = applied && entryId is { } id && versions.TryGetValue(id, out var v) ? RegistryRowVersion.Encode(v) : null;
            result.Add(new RegistryBatchRowResult(state.Item.ClientRowId, status, entryId, version, state.Errors));
        }

        int Count(string status) => result.Count(r => r.Status == status);
        return new RegistryBatchResult(applied, dryRun, Count("added"), Count("updated"), Count("deleted"), Count("unchanged"), result);
    }

    private static string MessageKey(IReadOnlyDictionary<string, object?>? details)
        => details?.GetValueOrDefault("messageKey") as string ?? "err.ECR-REG-0422.entryImportRowFailed";

    private static IEnumerable<(string, string?)> Params(IReadOnlyDictionary<string, object?>? details)
        => (details ?? new Dictionary<string, object?>())
            .Where(d => d.Key != "messageKey" && d.Value is string or long or int)
            .Select(d => (d.Key, Convert.ToString(d.Value, CultureInfo.InvariantCulture)));

    /// <summary>Код помилки з ключа каталогу: <c>err.ECR-REG-0422.x</c> → <c>ECR-REG-0422</c>.</summary>
    private static string CodeOf(string messageKey)
    {
        var match = CodeInKey().Match(messageKey);
        return match.Success ? match.Groups[1].Value : "ECR-REG-0422";
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^err\.(ECR-[A-Z]{3,4}-\d{4})\.")]
    private static partial Regex CodeInKey();

    /// <summary>Стан рядка пакета під час обробки.</summary>
    private sealed class RowState(RegistryBatchItemDto item)
    {
        public RegistryBatchItemDto Item { get; } = item;

        /// <summary>Наявний запис рядка.</summary>
        public RegistryEntry? Entry { get; set; }

        /// <summary>Код для writer'а: наявного запису або нового (порожньо — авто-код).</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Значення рядка з числами, розібраними за мовою користувача (<see cref="RegistryUserNumbers"/>).</summary>
        public IReadOnlyDictionary<string, object?> Values { get; set; } = new Dictionary<string, object?>();

        /// <summary>Видалення рядка виконано в транзакції пакета.</summary>
        public bool Deleted { get; set; }

        public List<RegistryBatchRowError> Errors { get; } = [];

        public void Fail(string? field, string errorCode, string messageKey, params (string Key, string? Value)[] parameters)
            => Errors.Add(new RegistryBatchRowError(
                field,
                errorCode,
                messageKey,
                parameters.Where(p => p.Value is not null).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value)));
    }

    /// <summary>Сигнал відкату транзакції пакета: <c>dryRun</c> або помилки рядків.</summary>
    private sealed class RollbackBatch : Exception
    {
    }
}
