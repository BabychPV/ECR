// src/Ecr.Application/GasComposition/GasCompositionService.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Flert;
using Ecr.Application.Registries.Keys;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Application.GasComposition;

/// <summary>
/// Сторінка «Склад газу» (D-NEW, людина 24.11): потоки, кейси і версії складу — власник ECR. Дані живуть у довідниках
/// <c>STREAM</c> → <c>STREAM_CASE</c> → <c>GAS_COMPOSITION</c> (форма, яку читають методології, не змінюється); писати їх
/// можна лише тут — загальні редактори довідників відмовляють (<see cref="ExternalRegistryGuard"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Кожна зміна — одна транзакція: сусідні версії, сама версія, рядки складу, подія журналу <see cref="ChangedEventType"/>.
/// Значення пише <see cref="RegistryEntryWriter"/> (типи, ключ «потік + кейс» у вікні, подія <c>RegistryValueChanged</c>).</item>
/// <item>Правила шкали — <see cref="GasCompositionRules"/>, значень — <see cref="GasCompositionValidation"/>; замкнений період
/// (закритий / затверджений, що читав склад) — відмова до будь-якого запису.</item>
/// <item>Після коміту — злиті задачі перерахунку залежних за зачеплені роки (лише роки з відкритими залежними; затверджені
/// аркуші задача пропускає).</item>
/// </list>
/// ⛔ Нічого не видаляється фізично: видалення — логічне, рядок складу, прибраний із версії, вимикається; старі значення
/// тримають системні темпоральні таблиці довідників.
/// </remarks>
public sealed partial class GasCompositionService(
    IGasCompositionStore store,
    IGasCompositionProvisioning provisioning,
    IRegistryStore registries,
    RegistryEntryWriter writer,
    RegistryKeyService keys,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IBackgroundJobScheduler jobs,
    IFlertSyncStatusReader importStatus,
    IClock clock,
    ILogger<GasCompositionService>? logger = null)
{
    /// <summary>Подія журналу безпеки: одна на дію людини.</summary>
    public const string ChangedEventType = "GasCompositionChanged";

    /// <summary>Ключ: потік не знайдено.</summary>
    public const string StreamNotFoundKey = "err.ECR-REG-0404.gasCompositionStream";

    /// <summary>Ключ: версію не знайдено.</summary>
    public const string VersionNotFoundKey = "err.ECR-REG-0404.gasCompositionVersion";

    /// <summary>Ключ: потік із такою назвою й розташуванням уже є.</summary>
    public const string StreamNameTakenKey = "err.ECR-REG-0409.gasCompositionStreamNameTaken";

    /// <summary>Ключ: кейс із такою назвою в потоці вже є.</summary>
    public const string CaseNameTakenKey = "err.ECR-REG-0409.gasCompositionCaseNameTaken";

    /// <summary>Ключ: порожнє або задовге текстове поле.</summary>
    public const string TextFieldKey = "err.ECR-REG-0422.gasCompositionText";

    /// <summary>Ключ: на потік посилаються дані поза складом газу.</summary>
    public const string StreamReferencedKey = "err.ECR-REG-0409.gasCompositionStreamReferenced";

    /// <summary>Ключ: імпорт неможливий, у ECR уже є дані.</summary>
    public const string AlreadyImportedKey = "err.ECR-REG-0409.gasCompositionAlreadyImported";

    /// <summary>Ціль злиття постановок імпорту.</summary>
    public const string ImportTarget = "gas-composition-import";

    /// <summary>Найдовша назва кейсу (FLERT <c>nvarchar(100)</c>).</summary>
    public const int MaxCaseName = 100;

    /// <summary>Найдовша назва потоку (FLERT <c>nvarchar(200)</c>).</summary>
    public const int MaxStreamName = 200;

    /// <summary>Поля потоку, які читають розрахунки (пошук потоку за номером, сушею, кислотністю, групою).</summary>
    private static readonly string[] CalcRelevantStreamFields =
    [
        FlertCompositionSchema.StreamNumber, FlertCompositionSchema.StreamIsOnshore, FlertCompositionSchema.StreamIsSour,
        FlertCompositionSchema.StreamIsHmb, FlertCompositionSchema.StreamGroup,
    ];

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    // ─── Читання ─────────────────────────────────────────────────────────────

    /// <summary>Перелік потоків, право змін і стан імпорту.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<GasStreamListDto> ListAsync(CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, GasCompositionPermissions.View, ct).ConfigureAwait(false);
        var canEdit = PermissionCheck.IsGranted(profile, GasCompositionPermissions.Edit);
        var canImport = canEdit && PermissionCheck.IsGranted(profile, CollectFromSourceHandler.Permission);

        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false);
        var streams = ids is null ? [] : await store.ListStreamsAsync(ids, ct).ConfigureAwait(false);
        var versions = ids is null ? [] : await store.ListVersionsAsync(ids, null, ct).ConfigureAwait(false);
        var byStream = versions.ToLookup(v => v.StreamEntryId);

        var items = streams
            .Select(s =>
            {
                var own = byStream[s.Id].ToList();
                return new GasStreamListItemDto(
                    s.Id,
                    s.StreamId,
                    s.Number,
                    s.Name ?? string.Empty,
                    s.IsOnshore,
                    s.IsSour,
                    s.IsHmb,
                    s.Hidden,
                    own.Select(v => v.CaseName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    own.Count,
                    own.Count == 0 ? null : own.Max(v => v.ValidFrom),
                    own.Count(v => Deviates(Sum(v))),
                    own.Count(v => Sum(v) is > 0m and < LowSum),
                    own.Count(v => Sum(v) == 0m));
            })
            .OrderBy(s => s.StreamId)
            .ToList();

        var status = await importStatus.ReadAsync(ct).ConfigureAwait(false);
        return new GasStreamListDto(items, canEdit, canImport, new GasImportStatusDto(streams.Count == 0, status.LastRun));
    }

    /// <summary>Компоненти для редактора.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<GasComponentDto>> ListComponentsAsync(CancellationToken ct)
    {
        _ = await PermissionCheck.RequireAsync(access, currentUser, GasCompositionPermissions.View, ct).ConfigureAwait(false);
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false);
        if (ids is null)
        {
            // Довідників ще немає: редактор показує компоненти FLERT, довідник заведе перший запис.
            return [.. FlertCompositionSchema.Components.Select(c => new GasComponentDto(c, c, null, null, GroupOf(c)))];
        }

        return [.. (await store.ListComponentsAsync(ids, ct).ConfigureAwait(false))
            .OrderBy(c => c.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new GasComponentDto(c.Code, c.Name, c.Formula, c.Mw, GroupOf(c.Code)))];
    }

    /// <summary>Потік з кейсами й версіями.</summary>
    /// <param name="streamEntryId">Запис потоку.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<GasStreamDetailDto> GetStreamAsync(long streamEntryId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, GasCompositionPermissions.View, ct).ConfigureAwait(false);
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false) ?? throw StreamNotFound(streamEntryId);
        return await DetailAsync(ids, streamEntryId, PermissionCheck.IsGranted(profile, GasCompositionPermissions.Edit), ct)
            .ConfigureAwait(false);
    }

    // ─── Потоки ──────────────────────────────────────────────────────────────

    /// <summary>Новий потік; Id = найбільший наявний + 1 (правило FLERT, Id ніколи не перевикористовується).</summary>
    /// <param name="request">Атрибути.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<GasChangeResultDto> CreateStreamAsync(GasStreamRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = await RequireEditAsync(ct).ConfigureAwait(false);
        var values = StreamValues(request, out var name);
        var ids = await provisioning.EnsureAsync(ct).ConfigureAwait(false);

        long created = 0;
        await uow.ExecuteInTransactionAsync(
            async tx =>
            {
                var streams = await store.ListStreamsAsync(ids, tx).ConfigureAwait(false);
                RequireUniqueName(streams, name, request.IsOnshore, exceptId: null);

                var streamId = await store.MaxStreamIdAsync(ids, tx).ConfigureAwait(false) + 1;
                values[FlertCompositionSchema.StreamLegacyId] = streamId;
                var code = FlertCompositionMapping.StreamCode(streamId);

                var result = await writer.WriteAsync(
                    new RegistryEntryWriteBatch(ids.Stream, [new RegistryEntryWrite(code, values) { DisplayName = name }]) { CreateOnly = true },
                    tx).ConfigureAwait(false);
                RequireApplied(result);
                created = result.Rows[0].Entry.Id;

                await LogAsync(userId, "createStream", $"{streamId} {name}", new() { ["location"] = request.IsOnshore ? "onshore" : "offshore", ["number"] = values[FlertCompositionSchema.StreamNumber] }, tx).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return new GasChangeResultDto(await DetailAsync(ids, created, canEdit: true, ct).ConfigureAwait(false), [], []);
    }

    /// <summary>Зміна атрибутів потоку (не версіонуються, як і у FLERT).</summary>
    /// <param name="streamEntryId">Запис потоку.</param>
    /// <param name="request">Атрибути.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Поля, які читають розрахунки (<see cref="CalcRelevantStreamFields"/>), змінюються лише коли жодна версія потоку не
    /// тримає замкненого періоду: інакше закритий звіт перерахувався б з іншим потоком. Назву й документ HMB — завжди.
    /// </remarks>
    public async Task<GasChangeResultDto> UpdateStreamAsync(long streamEntryId, GasStreamRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = await RequireEditAsync(ct).ConfigureAwait(false);
        var values = StreamValues(request, out var name);
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false) ?? throw StreamNotFound(streamEntryId);

        var calcChanged = false;
        await uow.ExecuteInTransactionAsync(
            async tx =>
            {
                var streams = await store.ListStreamsAsync(ids, tx).ConfigureAwait(false);
                var stream = streams.FirstOrDefault(s => s.Id == streamEntryId) ?? throw StreamNotFound(streamEntryId);
                RequireUniqueName(streams, name, request.IsOnshore, exceptId: streamEntryId);

                calcChanged = stream.Number != values[FlertCompositionSchema.StreamNumber] as string
                              || stream.IsOnshore != request.IsOnshore || stream.IsSour != request.IsSour
                              || stream.IsHmb != request.IsHmb || stream.Group != request.Group;
                if (calcChanged)
                {
                    var versions = await store.ListVersionsAsync(ids, streamEntryId, tx).ConfigureAwait(false);
                    await RequireUnlockedAsync(ids, versions.Select(v => GasCompositionRules.AffectedByEdit(v.Slot)), tx).ConfigureAwait(false);
                }

                if (!string.Equals(stream.Name, name, StringComparison.Ordinal)
                    && await registries.FindEntryAsync(streamEntryId, tx).ConfigureAwait(false) is { } entry)
                {
                    entry.Rename(Display(name));
                }

                var result = await writer.UpdateAsync(
                    new RegistryEntryUpdateBatch(ids.Stream, [new RegistryEntryUpdate(streamEntryId, values)]), tx).ConfigureAwait(false);
                RequireNoErrors(result);
                await uow.SaveChangesAsync(tx).ConfigureAwait(false);

                await LogAsync(userId, "updateStream", Label(stream), new()
                {
                    ["before"] = new { stream.Name, stream.Number, stream.IsOnshore, stream.IsSour, stream.IsHmb, stream.HmbDoc, stream.Group, stream.Hidden },
                    ["after"] = new { Name = name, request.Number, request.IsOnshore, request.IsSour, request.IsHmb, request.HmbDoc, request.Group, request.Hidden },
                }, tx).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var (queued, failed) = calcChanged
            ? await EnqueueAsync(ids, null, [ids.Stream], userId, $"Gas composition: stream {streamEntryId} changed", ct).ConfigureAwait(false)
            : ([], []);
        return new GasChangeResultDto(await DetailAsync(ids, streamEntryId, canEdit: true, ct).ConfigureAwait(false), queued, failed);
    }

    /// <summary>Логічне видалення потоку разом з усіма версіями й рядками складу.</summary>
    /// <param name="streamEntryId">Запис потоку.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<GasChangeResultDto> DeleteStreamAsync(long streamEntryId, CancellationToken ct)
    {
        var userId = await RequireEditAsync(ct).ConfigureAwait(false);
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false) ?? throw StreamNotFound(streamEntryId);

        GasDateRange? affected = null;
        await uow.ExecuteInTransactionAsync(
            async tx =>
            {
                var streams = await store.ListStreamsAsync(ids, tx).ConfigureAwait(false);
                var stream = streams.FirstOrDefault(s => s.Id == streamEntryId) ?? throw StreamNotFound(streamEntryId);
                var versions = await store.ListVersionsAsync(ids, streamEntryId, tx).ConfigureAwait(false);
                await RequireUnlockedAsync(ids, versions.Select(v => GasCompositionRules.AffectedByEdit(v.Slot)), tx).ConfigureAwait(false);
                affected = Union(versions.Select(v => GasCompositionRules.AffectedByEdit(v.Slot)));

                // Посилання ЗЗОВНІ набору (комірки документів, константи методологій): склад самого потоку не рахується.
                var set = new List<long> { streamEntryId };
                set.AddRange(versions.Select(v => v.Id));
                set.AddRange(versions.SelectMany(v => v.Rows).Select(r => r.Id));
                var references = await registries.CountReferencesFromOutsideAsync(set, tx).ConfigureAwait(false);
                if (references.Total > 0)
                {
                    throw GasCompositionRules.Rule(ErrorCodes.RegistryEntryInUse, StreamReferencedKey,
                        $"Потік {stream.StreamId} не видаляється: на нього посилаються дані ({references.Total}).",
                        ("streamId", stream.StreamId.ToString(CultureInfo.InvariantCulture)),
                        ("references", references.Total.ToString(CultureInfo.InvariantCulture)));
                }

                foreach (var version in versions)
                {
                    await SoftDeleteVersionAsync(ids, version, userId, tx).ConfigureAwait(false);
                }

                await SoftDeleteAsync(ids.Stream, [streamEntryId], userId, tx).ConfigureAwait(false);
                await LogAsync(userId, "deleteStream", Label(stream), new() { ["versions"] = versions.Select(Snapshot).ToList() }, tx)
                    .ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var (queued, failed) = affected is null
            ? ([], [])
            : await EnqueueAsync(ids, affected, ids.All, userId, $"Gas composition: stream {streamEntryId} deleted", ct).ConfigureAwait(false);
        return new GasChangeResultDto(null, queued, failed);
    }

    // ─── Кейси й версії ──────────────────────────────────────────────────────

    /// <summary>Нова версія кейсу з місяця <see cref="GasVersionCreateRequest.ValidFrom"/> (або перша версія нового кейсу).</summary>
    /// <param name="streamEntryId">Потік.</param>
    /// <param name="request">Версія.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<GasChangeResultDto> CreateVersionAsync(long streamEntryId, GasVersionCreateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = await RequireEditAsync(ct).ConfigureAwait(false);
        var caseName = RequireText(request.CaseName, "caseName", MaxCaseName)!;
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false) ?? throw StreamNotFound(streamEntryId);

        GasDateRange? affected = null;
        await uow.ExecuteInTransactionAsync(
            async tx =>
            {
                var stream = (await store.ListStreamsAsync(ids, tx).ConfigureAwait(false)).FirstOrDefault(s => s.Id == streamEntryId)
                    ?? throw StreamNotFound(streamEntryId);
                var versions = await store.ListVersionsAsync(ids, streamEntryId, tx).ConfigureAwait(false);
                var timeline = versions.Where(v => SameCase(v.CaseName, caseName)).ToList();

                var plan = GasCompositionRules.PlanInsert([.. timeline.Select(v => v.Slot)], request.ValidFrom);
                await RequireUnlockedAsync(ids, [plan.Affected], tx).ConfigureAwait(false);
                var validated = await ValidateAsync(ids, Values(request), tx).ConfigureAwait(false);
                affected = plan.Affected;

                // Назву кейсу беремо з наявних версій (регістр як був), правило FLERT — з версії, яку нова продовжує.
                var host = timeline.Where(v => v.Slot.From < request.ValidFrom).MaxBy(v => v.Slot.From) ?? timeline.MinBy(v => v.Slot.From);
                var name = timeline.FirstOrDefault()?.CaseName ?? caseName;

                await AdjustAsync(ids, plan.Adjustments, tx).ConfigureAwait(false);
                var code = await NewVersionCodeAsync(ids, timeline, request.ValidFrom, tx).ConfigureAwait(false);
                var caseValues = CaseValues(streamEntryId, name, Values(request), validated);
                caseValues[FlertCompositionSchema.CaseRuleParse] = host?.RuleParse;
                var result = await writer.WriteAsync(
                    new RegistryEntryWriteBatch(ids.Case, [new RegistryEntryWrite(code, caseValues) { DisplayName = name, Validity = plan.NewWindow }])
                    {
                        CreateOnly = true,
                    },
                    tx).ConfigureAwait(false);
                RequireApplied(result);
                var versionId = result.Rows[0].Entry.Id;

                await WriteRowsAsync(ids, versionId, code, [], validated.Components, tx).ConfigureAwait(false);
                await LogAsync(userId, "createVersion", Label(stream), new()
                {
                    ["case"] = name,
                    ["window"] = Window(plan.NewWindow!.Value),
                    ["previousNow"] = plan.Adjustments.Select(a => Window(a.Window)).FirstOrDefault(),
                    ["sum"] = validated.Sum,
                    ["components"] = validated.Components,
                }, tx).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var (queued, failed) = await EnqueueAsync(ids, affected, [ids.Case, ids.Composition], userId,
            $"Gas composition: new version of {streamEntryId}", ct).ConfigureAwait(false);
        return new GasChangeResultDto(await DetailAsync(ids, streamEntryId, canEdit: true, ct).ConfigureAwait(false), queued, failed);
    }

    /// <summary>Зміна версії: значення і/або місяць початку.</summary>
    /// <param name="versionId">Версія.</param>
    /// <param name="request">Нові значення (повний склад).</param>
    /// <param name="ct">Скасування.</param>
    public async Task<GasChangeResultDto> UpdateVersionAsync(long versionId, GasVersionUpdateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = await RequireEditAsync(ct).ConfigureAwait(false);
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false) ?? throw VersionNotFound(versionId);

        long streamEntryId = 0;
        GasDateRange? affected = null;
        await uow.ExecuteInTransactionAsync(
            async tx =>
            {
                var (version, timeline) = await FindVersionAsync(ids, versionId, tx).ConfigureAwait(false);
                streamEntryId = version.StreamEntryId;
                var (values, validated) = KeepPrecision(version, Values(request), await ValidateAsync(ids, Values(request), tx).ConfigureAwait(false));

                var valuesChanged = !SameValues(version, values, validated);
                var moved = version.ValidFrom != request.ValidFrom;
                if (!valuesChanged && !moved)
                {
                    return;
                }

                var plan = moved
                    ? GasCompositionRules.PlanMove([.. timeline.Select(v => v.Slot)], versionId, request.ValidFrom, valuesChanged)
                    : new GasTimelinePlan(version.Slot.Window, [], GasCompositionRules.AffectedByEdit(version.Slot));
                await RequireUnlockedAsync(ids, [plan.Affected], tx).ConfigureAwait(false);
                affected = plan.Affected;

                // Перенесення на пізніше: спершу сама версія (звільнити місце), потім сусід; на раніше — навпаки.
                var later = moved && request.ValidFrom > version.ValidFrom;
                if (!later)
                {
                    await AdjustAsync(ids, plan.Adjustments, tx).ConfigureAwait(false);
                }

                var caseValues = CaseValues(version.StreamEntryId, version.CaseName, values, validated);
                var result = await writer.UpdateAsync(
                    new RegistryEntryUpdateBatch(ids.Case, [new RegistryEntryUpdate(versionId, caseValues) { Validity = plan.NewWindow }]), tx)
                    .ConfigureAwait(false);
                RequireNoErrors(result);

                if (later)
                {
                    await AdjustAsync(ids, plan.Adjustments, tx).ConfigureAwait(false);
                }

                await WriteRowsAsync(ids, versionId, version.Code, version.Rows, validated.Components, tx).ConfigureAwait(false);
                await LogAsync(userId, "updateVersion", await LabelAsync(ids, version.StreamEntryId, tx).ConfigureAwait(false), new()
                {
                    ["case"] = version.CaseName,
                    ["before"] = Snapshot(version),
                    ["after"] = new
                    {
                        window = Window(plan.NewWindow!.Value),
                        t = values.TemperatureC,
                        density = values.DensityOp,
                        mw = values.MwHysys,
                        z = values.Z,
                        sum = validated.Sum,
                        components = validated.Components,
                    },
                    ["neighbourNow"] = plan.Adjustments.Select(a => Window(a.Window)).FirstOrDefault(),
                }, tx).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var (queued, failed) = await EnqueueAsync(ids, affected, [ids.Case, ids.Composition], userId,
            $"Gas composition: version {versionId} changed", ct).ConfigureAwait(false);
        return new GasChangeResultDto(await DetailAsync(ids, streamEntryId, canEdit: true, ct).ConfigureAwait(false), queued, failed);
    }

    /// <summary>Видалення версії; попередня (якщо стикується) продовжується на її вікно.</summary>
    /// <param name="versionId">Версія.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<GasChangeResultDto> DeleteVersionAsync(long versionId, CancellationToken ct)
    {
        var userId = await RequireEditAsync(ct).ConfigureAwait(false);
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false) ?? throw VersionNotFound(versionId);

        long streamEntryId = 0;
        GasDateRange? affected = null;
        await uow.ExecuteInTransactionAsync(
            async tx =>
            {
                var (version, timeline) = await FindVersionAsync(ids, versionId, tx).ConfigureAwait(false);
                streamEntryId = version.StreamEntryId;
                var plan = GasCompositionRules.PlanDelete([.. timeline.Select(v => v.Slot)], versionId);
                await RequireUnlockedAsync(ids, [plan.Affected], tx).ConfigureAwait(false);
                affected = plan.Affected;

                // Спершу видалити (звільнити ключ у вікні), потім продовжити попередню.
                await SoftDeleteVersionAsync(ids, version, userId, tx).ConfigureAwait(false);
                await AdjustAsync(ids, plan.Adjustments, tx).ConfigureAwait(false);
                await LogAsync(userId, "deleteVersion", await LabelAsync(ids, version.StreamEntryId, tx).ConfigureAwait(false), new()
                {
                    ["case"] = version.CaseName,
                    ["deleted"] = Snapshot(version),
                    ["previousNow"] = plan.Adjustments.Select(a => Window(a.Window)).FirstOrDefault(),
                }, tx).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var (queued, failed) = await EnqueueAsync(ids, affected, [ids.Case, ids.Composition], userId,
            $"Gas composition: version {versionId} deleted", ct).ConfigureAwait(false);
        return new GasChangeResultDto(await DetailAsync(ids, streamEntryId, canEdit: true, ct).ConfigureAwait(false), queued, failed);
    }

    /// <summary>Перейменування кейсу: усі його версії; лише коли жодна не тримає замкненого періоду.</summary>
    /// <param name="streamEntryId">Потік.</param>
    /// <param name="request">Стара й нова назва.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<GasChangeResultDto> RenameCaseAsync(long streamEntryId, GasCaseRenameRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = await RequireEditAsync(ct).ConfigureAwait(false);
        var to = RequireText(request.To, "caseName", MaxCaseName)!;
        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false) ?? throw StreamNotFound(streamEntryId);

        GasDateRange? affected = null;
        await uow.ExecuteInTransactionAsync(
            async tx =>
            {
                var versions = await store.ListVersionsAsync(ids, streamEntryId, tx).ConfigureAwait(false);
                var timeline = versions.Where(v => SameCase(v.CaseName, request.From)).ToList();
                if (timeline.Count == 0)
                {
                    throw StreamNotFound(streamEntryId);
                }

                if (string.Equals(timeline[0].CaseName, to, StringComparison.Ordinal))
                {
                    return;
                }

                if (!SameCase(request.From, to) && versions.Any(v => SameCase(v.CaseName, to)))
                {
                    throw GasCompositionRules.Rule(ErrorCodes.RegistryEntryInUse, CaseNameTakenKey,
                        $"Кейс «{to}» у цьому потоці вже є.", ("caseName", to));
                }

                await RequireUnlockedAsync(ids, timeline.Select(v => GasCompositionRules.AffectedByEdit(v.Slot)), tx).ConfigureAwait(false);
                affected = Union(timeline.Select(v => GasCompositionRules.AffectedByEdit(v.Slot)));

                foreach (var version in timeline)
                {
                    if (await registries.FindEntryAsync(version.Id, tx).ConfigureAwait(false) is { } entry)
                    {
                        entry.Rename(Display(to));
                    }
                }

                var result = await writer.UpdateAsync(
                    new RegistryEntryUpdateBatch(ids.Case, [.. timeline.Select(v => new RegistryEntryUpdate(
                        v.Id, new Dictionary<string, object?>(StringComparer.Ordinal) { [FlertCompositionSchema.CaseName] = to }))]),
                    tx).ConfigureAwait(false);
                RequireNoErrors(result);
                await uow.SaveChangesAsync(tx).ConfigureAwait(false);
                await LogAsync(userId, "renameCase", await LabelAsync(ids, streamEntryId, tx).ConfigureAwait(false), new() { ["from"] = timeline[0].CaseName, ["to"] = to, ["versions"] = timeline.Count }, tx).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var (queued, failed) = await EnqueueAsync(ids, affected, [ids.Case], userId,
            $"Gas composition: case renamed in {streamEntryId}", ct).ConfigureAwait(false);
        return new GasChangeResultDto(await DetailAsync(ids, streamEntryId, canEdit: true, ct).ConfigureAwait(false), queued, failed);
    }

    // ─── Імпорт ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Разовий імпорт складу з FLERT (лише читання FLERT): ставить задачу. Можливий, поки в ECR немає жодного потоку — тож
    /// ніколи не перетирає правок ECR.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    public async Task<string> StartImportAsync(CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, GasCompositionPermissions.Edit, ct).ConfigureAwait(false);
        _ = await PermissionCheck.RequireAsync(access, currentUser, CollectFromSourceHandler.Permission, ct).ConfigureAwait(false);

        var ids = await store.FindRegistriesAsync(ct).ConfigureAwait(false);
        if (ids is not null && (await store.ListStreamsAsync(ids, ct).ConfigureAwait(false)).Count > 0)
        {
            throw AlreadyImported();
        }

        return await jobs.EnqueueCoalescedAsync<IFlertCompositionSyncJob>(ImportTarget, null, ct, profile.UserId).ConfigureAwait(false);
    }

    /// <summary>Відмова «імпорт уже був / у ECR уже є дані».</summary>
    public static BusinessRuleException AlreadyImported()
        => GasCompositionRules.Rule(ErrorCodes.RegistryEntryInUse, AlreadyImportedKey,
            "У ECR уже є потоки складу газу: імпорт із FLERT перетер би правки ECR, тому він можливий лише в порожню базу.");

    // ─── Допоміжне ───────────────────────────────────────────────────────────

    /// <summary>Група компонента для показу в редакторі.</summary>
    /// <param name="code">Код компонента.</param>
    public static string GroupOf(string code) => code switch
    {
        "Nitrogen" or "CO2" or "H2O" or "O2" or "H2" or "CO" or "NH3" => "inert",
        "Methane" or "Ethane" or "Propane" or "IC4_1" or "NC4_1" or "IC5_1" or "NC5_1" or "C6_1" or "C7_1" or "C8_1"
            or "C9_1" or "C10_1" or "C11_1" or "C12_1" or "C13_1" or "C14_1" or "CN1_2" or "CN2_2" or "CN3_1" => "hydrocarbon",
        "Benzene" or "Toluene" or "pXylene" or "EBenzene" => "aromatic",
        "H2S" or "MMercaptan" or "EMercaptan" or "nPMercaptan" or "nBMercaptan" or "CS2" or "COS" or "SO2" or "Sulfur"
            or "C2H6S2" or "C3H8S2" or "C4H10S2" => "sulphur",
        _ => "other",
    };

    /// <summary>Межа «явно неповного складу» для позначки в переліку (FLERT: 7 кейсів із Σ &lt; 90 %).</summary>
    private const decimal LowSum = 90m;

    private static bool Deviates(decimal sum) => sum == 0m || Math.Abs(sum - 100m) > GasCompositionRules.SumTolerance;

    private static decimal Sum(GasVersionRecord version) => version.ActiveRows.Sum(r => r.MolPct);

    private static bool SameCase(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static GasVersionValues Values(GasVersionCreateRequest r)
        => new(r.TemperatureC, r.DensityOp, r.MwHysys, r.Z, r.Components ?? [], r.AcceptSumDeviation);

    private static GasVersionValues Values(GasVersionUpdateRequest r)
        => new(r.TemperatureC, r.DensityOp, r.MwHysys, r.Z, r.Components ?? [], r.AcceptSumDeviation);

    private static LocalizedText Display(string text)
        => new(new Dictionary<string, string> { [UiStringResolver.DefaultLanguage] = text });

    private static string Window(ValidityWindow window)
        => $"[{window.FromInclusive?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-∞"}, "
           + $"{window.ToExclusive?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "∞"})";

    private static object Snapshot(GasVersionRecord v) => new
    {
        v.CaseName,
        window = Window(v.Slot.Window),
        t = v.TemperatureC,
        density = v.DensityOp,
        mw = v.MwHysys,
        v.Z,
        components = v.ActiveRows.ToDictionary(r => r.Component, r => r.MolPct, StringComparer.Ordinal),
    };

    private static GasDateRange? Union(IEnumerable<GasDateRange> ranges)
    {
        var list = ranges.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var to = list.Any(r => r.To is null) ? (DateOnly?)null : list.Max(r => r.To!.Value);
        return new GasDateRange(list.Min(r => r.From), to);
    }

    private static string? RequireText(string? value, string field, int max, bool required = true)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (!required)
            {
                return null;
            }

            throw GasCompositionRules.Rule(ErrorCodes.RegistrySwitchInOpenPeriod, TextFieldKey,
                $"Поле «{field}» обов'язкове.", ("field", field), ("max", max.ToString(CultureInfo.InvariantCulture)));
        }

        if (text.Length > max)
        {
            throw GasCompositionRules.Rule(ErrorCodes.RegistrySwitchInOpenPeriod, TextFieldKey,
                $"Поле «{field}» довше за {max} символів.", ("field", field), ("max", max.ToString(CultureInfo.InvariantCulture)));
        }

        return text;
    }

    private static Dictionary<string, object?> StreamValues(GasStreamRequest request, out string name)
    {
        name = RequireText(request.Name, "name", MaxStreamName)!;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [FlertCompositionSchema.StreamNumber] = RequireText(request.Number, "number", 100, required: false),
            [FlertCompositionSchema.StreamName] = name,
            [FlertCompositionSchema.StreamIsOnshore] = request.IsOnshore,
            [FlertCompositionSchema.StreamIsSour] = request.IsSour,
            [FlertCompositionSchema.StreamIsHmb] = request.IsHmb,
            [FlertCompositionSchema.StreamHmbDoc] = RequireText(request.HmbDoc, "hmbDoc", 255, required: false),
            [FlertCompositionSchema.StreamGroup] = request.Group,
            [FlertCompositionSchema.StreamExcludeInList] = request.Hidden,
        };
    }

    private static void RequireUniqueName(IReadOnlyList<GasStreamRecord> streams, string name, bool isOnshore, long? exceptId)
    {
        if (streams.Any(s => s.Id != exceptId && s.IsOnshore == isOnshore && string.Equals(s.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw GasCompositionRules.Rule(ErrorCodes.RegistryEntryInUse, StreamNameTakenKey,
                $"Потік «{name}» ({(isOnshore ? "суша" : "море")}) уже є.", ("name", name));
        }
    }

    private static Dictionary<string, object?> CaseValues(long streamEntryId, string caseName, GasVersionValues values, GasValidatedValues validated)
        => new(StringComparer.Ordinal)
        {
            [FlertCompositionSchema.CaseStream] = streamEntryId,
            [FlertCompositionSchema.CaseName] = caseName,
            [FlertCompositionSchema.CaseTemperature] = values.TemperatureC,
            [FlertCompositionSchema.CaseDensityOp] = values.DensityOp,
            [FlertCompositionSchema.CaseMwHysys] = values.MwHysys,
            [FlertCompositionSchema.CaseZ] = values.Z,
            [FlertCompositionSchema.CaseNoComposition] = validated.NoComposition,
        };

    /// <summary>
    /// Найменша різниця, яку вважаємо зміною. ⚠ Клієнт (браузер) читає <c>decimal(20,16)</c> як число JavaScript (~15–17
    /// значущих цифр) і шле назад НЕзмінене поле вже округленим: без допуску «змінити лише дату» переписувало б кожен
    /// компонент FLERT округленим значенням, і точність імпорту тихо губилася б.
    /// </summary>
    private const decimal Epsilon = 0.000000000001m;

    private static bool Close(decimal? a, decimal? b)
        => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) <= Epsilon * Math.Max(1m, Math.Abs(a.Value));

    /// <summary>Значення, яке писати: незмінене (у межах <see cref="Epsilon"/>) — збережене, повної точності.</summary>
    private static decimal? Keep(decimal? stored, decimal? incoming) => Close(stored, incoming) ? stored : incoming;

    /// <summary>Значення запиту з точністю збережених там, де людина їх не змінювала (<see cref="Epsilon"/>).</summary>
    private static (GasVersionValues Values, GasValidatedValues Validated) KeepPrecision(
        GasVersionRecord version, GasVersionValues values, GasValidatedValues validated)
    {
        var stored = version.ActiveRows.ToDictionary(r => r.Component, r => r.MolPct, StringComparer.OrdinalIgnoreCase);
        var components = validated.Components.ToDictionary(
            c => c.Key,
            c => stored.TryGetValue(c.Key, out var old) && Close(old, c.Value) ? old : c.Value,
            StringComparer.OrdinalIgnoreCase);
        return (
            values with
            {
                TemperatureC = Keep(version.TemperatureC, values.TemperatureC),
                DensityOp = Keep(version.DensityOp, values.DensityOp),
                MwHysys = Keep(version.MwHysys, values.MwHysys),
                Z = Keep(version.Z, values.Z),
            },
            validated with { Components = components, Sum = components.Values.Sum() });
    }

    private static bool SameValues(GasVersionRecord version, GasVersionValues values, GasValidatedValues validated)
    {
        var current = version.ActiveRows.ToDictionary(r => r.Component, r => r.MolPct, StringComparer.OrdinalIgnoreCase);
        return Close(version.TemperatureC, values.TemperatureC)
               && Close(version.DensityOp, values.DensityOp)
               && Close(version.MwHysys, values.MwHysys)
               && Close(version.Z, values.Z)
               && current.Count == validated.Components.Count
               && validated.Components.All(c => current.TryGetValue(c.Key, out var v) && Close(v, c.Value));
    }

    private static void RequireApplied(RegistryEntryWriteResult result)
    {
        RequireNoErrors(result);
        if (result.Rows.Count == 0)
        {
            throw new InvalidOperationException("Запис довідника не повернув рядка.");
        }
    }

    private static void RequireNoErrors(RegistryEntryWriteResult result)
    {
        if (result.Errors.Count == 0)
        {
            return;
        }

        var error = result.Errors[0];
        var details = new Dictionary<string, object?> { ["messageKey"] = error.MessageKey, ["field"] = error.Field, ["code"] = error.Key };
        foreach (var (key, value) in error.Params ?? new Dictionary<string, string>())
        {
            details[key] = value;
        }

        var code = error.MessageKey.StartsWith("err.ECR-REG-0409", StringComparison.Ordinal) || error.MessageKey.Contains("4092", StringComparison.Ordinal)
            ? ErrorCodes.RegistryEntryInUse
            : ErrorCodes.RegistrySwitchInOpenPeriod;
        throw new BusinessRuleException(code, $"Запис відхилено: {error.MessageKey} ({error.Field}).", details);
    }

    private static NotFoundException StreamNotFound(long id) => NotFound(StreamNotFoundKey, $"Потоку {id} немає.", id);

    private static NotFoundException VersionNotFound(long id) => NotFound(VersionNotFoundKey, $"Версії складу {id} немає.", id);

    private static NotFoundException NotFound(string messageKey, string message, long id)
        => new(ErrorCodes.RegistryEntryNotFound, message, new Dictionary<string, object?>
        {
            ["messageKey"] = messageKey,
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
        });

    private async Task<int> RequireEditAsync(CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, GasCompositionPermissions.Edit, ct).ConfigureAwait(false);
        return profile.UserId;
    }

    private async Task<GasValidatedValues> ValidateAsync(GasRegistryIds ids, GasVersionValues values, CancellationToken ct)
    {
        var known = (await store.ListComponentsAsync(ids, ct).ConfigureAwait(false))
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GasCompositionValidation.Validate(values, known);
    }

    private async Task RequireUnlockedAsync(GasRegistryIds ids, IEnumerable<GasDateRange> ranges, CancellationToken ct)
    {
        var list = ranges.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var locked = await store.ListLockedPeriodsAsync(ids, ct).ConfigureAwait(false);
        foreach (var range in list)
        {
            GasCompositionRules.RequireUnlocked(range, locked);
        }
    }

    private async Task<(GasVersionRecord Version, IReadOnlyList<GasVersionRecord> Timeline)> FindVersionAsync(
        GasRegistryIds ids, long versionId, CancellationToken ct)
    {
        var entry = await registries.FindEntryAsync(versionId, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != ids.Case)
        {
            throw VersionNotFound(versionId);
        }

        var streamEntryId = (await registries.ListValuesAsync(versionId, ct).ConfigureAwait(false))
            .Select(v => v.ValueRefEntryId)
            .FirstOrDefault(v => v is not null);
        if (streamEntryId is not { } streamId)
        {
            throw VersionNotFound(versionId);
        }

        var versions = await store.ListVersionsAsync(ids, streamId, ct).ConfigureAwait(false);
        var version = versions.FirstOrDefault(v => v.Id == versionId) ?? throw VersionNotFound(versionId);
        return (version, [.. versions.Where(v => SameCase(v.CaseName, version.CaseName))]);
    }

    private async Task AdjustAsync(GasRegistryIds ids, IReadOnlyList<GasVersionAdjustment> adjustments, CancellationToken ct)
    {
        if (adjustments.Count == 0)
        {
            return;
        }

        var result = await writer.UpdateAsync(
            new RegistryEntryUpdateBatch(ids.Case, [.. adjustments.Select(a => new RegistryEntryUpdate(a.Id, new Dictionary<string, object?>()) { Validity = a.Window })]),
            ct).ConfigureAwait(false);
        RequireNoErrors(result);
    }

    /// <summary>Код нової версії: <c>C{номер кейсу}_{yyyyMMdd}</c>; зайнятий (видалена версія тієї ж дати) — з суфіксом.</summary>
    private async Task<string> NewVersionCodeAsync(GasRegistryIds ids, IReadOnlyList<GasVersionRecord> timeline, DateOnly from, CancellationToken ct)
    {
        var number = timeline.Select(v => CaseNumber(v.Code)).FirstOrDefault(n => n is not null);
        var all = await store.ListCodesAsync(ids.Case, "C", ct).ConfigureAwait(false);
        if (number is null)
        {
            number = all.Select(CaseNumber).Where(n => n is not null).Select(n => n!.Value).DefaultIfEmpty(0).Max() + 1;
        }

        var taken = all.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var code = FlertCompositionMapping.CaseCode(number.Value, from);
        for (var k = 2; taken.Contains(code); k++)
        {
            code = FlertCompositionMapping.CaseCode(number.Value, from) + "_" + k.ToString(CultureInfo.InvariantCulture);
        }

        return code;
    }

    private static int? CaseNumber(string code)
    {
        if (code.Length < 2 || code[0] is not ('C' or 'c'))
        {
            return null;
        }

        var end = code.IndexOf('_', StringComparison.Ordinal);
        var digits = end < 0 ? code[1..] : code[1..end];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>
    /// Рядки складу версії: змінене — оновити, нове — додати (вимкнений рядок того ж компонента — увімкнути), прибране —
    /// вимкнути (не видаляти: код і ключ лишаються за рядком, історія читабельна).
    /// </summary>
    private async Task WriteRowsAsync(
        GasRegistryIds ids, long versionId, string versionCode, IReadOnlyList<GasRowRecord> existing,
        IReadOnlyDictionary<string, decimal> components, CancellationToken ct)
    {
        var byComponent = existing.ToDictionary(r => r.Component, StringComparer.OrdinalIgnoreCase);
        var componentIds = (await store.ListComponentsAsync(ids, ct).ConfigureAwait(false))
            .ToDictionary(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase);

        var updates = new List<RegistryEntryUpdate>();
        var creates = new List<RegistryEntryWrite>();
        foreach (var (component, value) in components)
        {
            if (byComponent.TryGetValue(component, out var row))
            {
                if (!row.IsActive || !Close(row.MolPct, value))
                {
                    updates.Add(new RegistryEntryUpdate(row.Id, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [FlertCompositionSchema.RowMolPct] = value,
                    })
                    { IsActive = true });
                }
            }
            else
            {
                creates.Add(new RegistryEntryWrite($"{versionCode}_{component}", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [FlertCompositionSchema.RowCase] = versionId,
                    [FlertCompositionSchema.RowComponent] = componentIds[component],
                    [FlertCompositionSchema.RowMolPct] = value,
                }));
            }
        }

        updates.AddRange(existing
            .Where(r => r.IsActive && !components.ContainsKey(r.Component))
            .Select(r => new RegistryEntryUpdate(r.Id, new Dictionary<string, object?>()) { IsActive = false }));

        if (updates.Count > 0)
        {
            RequireNoErrors(await writer.UpdateAsync(new RegistryEntryUpdateBatch(ids.Composition, updates), ct).ConfigureAwait(false));
        }

        if (creates.Count > 0)
        {
            RequireNoErrors(await writer.WriteAsync(new RegistryEntryWriteBatch(ids.Composition, creates) { CreateOnly = true }, ct)
                .ConfigureAwait(false));
        }
    }

    private async Task SoftDeleteVersionAsync(GasRegistryIds ids, GasVersionRecord version, int userId, CancellationToken ct)
    {
        await SoftDeleteAsync(ids.Composition, [.. version.Rows.Select(r => r.Id)], userId, ct).ConfigureAwait(false);
        await SoftDeleteAsync(ids.Case, [version.Id], userId, ct).ConfigureAwait(false);
    }

    /// <summary>Логічне видалення записів одного довідника: ключі звільняються, ревізія даних рухається.</summary>
    private async Task SoftDeleteAsync(int registryDefId, IReadOnlyList<long> entryIds, int userId, CancellationToken ct)
    {
        if (entryIds.Count == 0)
        {
            return;
        }

        var entries = (await registries.FindEntriesByIdsAsync(entryIds, ct).ConfigureAwait(false))
            .Where(e => !e.IsDeleted && e.RegistryDefId == registryDefId)
            .ToList();
        foreach (var entry in entries)
        {
            entry.SoftDelete(userId, clock.UtcNow);
        }

        await keys.ReleaseManyAsync(entries, ct).ConfigureAwait(false);
        if (await registries.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false) is { } definition)
        {
            definition.BumpDataRevision();
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Потік у журналі: «Id назва» — те, що людина знає, а не Id запису довідника.</summary>
    private static string Label(GasStreamRecord stream) => $"{stream.StreamId} {stream.Name}";

    private async Task<string> LabelAsync(GasRegistryIds ids, long streamEntryId, CancellationToken ct)
        => (await store.ListStreamsAsync(ids, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == streamEntryId) is { } stream
            ? Label(stream)
            : streamEntryId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Подія <see cref="ChangedEventType"/>: плоский JSON «operation, stream, …» — журнал безпеки показує його парами
    /// «ключ: значення» (auditDetails), тож тут лише людські поля: Id і назва потоку, кейс, вікна дати, Σ, компоненти; Id записів
    /// довідника не пишуться (у журналі значень <c>RegistryValueChanged</c> вони й так є).
    /// </summary>
    private Task LogAsync(int userId, string operation, string stream, Dictionary<string, object?> fields, CancellationToken ct)
    {
        var details = new Dictionary<string, object?> { ["operation"] = operation, ["stream"] = stream };
        foreach (var (key, value) in fields)
        {
            details[key] = value;
        }

        return audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                ChangedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(details),
                userId,
                currentUser.CorrelationId),
            ct);
    }
    /// <summary>
    /// Після коміту: злита задача перерахунку на довідник × рік, лише роки з відкритими залежними, перетнуті із зачепленими
    /// датами. Збій черги не валить зміну (вона вже закомічена) — рік у <c>failed</c>, запис у журнал.
    /// </summary>
    private async Task<(IReadOnlyList<int> Queued, IReadOnlyList<int> Failed)> EnqueueAsync(
        GasRegistryIds ids, GasDateRange? affected, IReadOnlyList<int> registryIds, int userId, string reason, CancellationToken ct)
    {
        var dependents = await store.ListDependentYearsAsync(ids, ct).ConfigureAwait(false);
        var queued = new SortedSet<int>();
        var failed = new SortedSet<int>();
        foreach (var registryId in registryIds.Distinct())
        {
            if (!dependents.TryGetValue(registryId, out var years) || years.Count == 0)
            {
                continue;
            }

            var hit = affected is null ? years : GasCompositionRules.YearsOf(affected, years.Max()).Intersect(years).ToList();
            foreach (var year in hit)
            {
                try
                {
                    await jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                            RegistryImpactRecalculationTarget.ForYear(registryId, year),
                            new RegistryImpactRecalculationRequest(registryId, [], reason, year),
                            ct,
                            createdByUserId: userId)
                        .ConfigureAwait(false);
                    queued.Add(year);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(year);
                    LogEnqueueFailed(_logger, ex, registryId, year);
                }
            }
        }

        return ([.. queued], [.. failed]);
    }

    private async Task<GasStreamDetailDto> DetailAsync(GasRegistryIds ids, long streamEntryId, bool canEdit, CancellationToken ct)
    {
        var stream = (await store.ListStreamsAsync(ids, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == streamEntryId)
                     ?? throw StreamNotFound(streamEntryId);
        var versions = await store.ListVersionsAsync(ids, streamEntryId, ct).ConfigureAwait(false);
        var locked = versions.Count == 0 ? [] : await store.ListLockedPeriodsAsync(ids, ct).ConfigureAwait(false);

        var cases = versions
            .GroupBy(v => v.CaseName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new GasCaseDto(
                g.First().CaseName,
                [.. g.OrderBy(v => v.Slot.From).Select(v => new GasVersionDto(
                    v.Id,
                    v.Slot.From,
                    v.ValidTo,
                    v.TemperatureC,
                    v.DensityOp,
                    v.MwHysys,
                    v.Z,
                    v.RuleParse,
                    v.LegacyTypeId,
                    v.LegacyCompositionId,
                    v.FlertAliases,
                    Sum(v),
                    !v.ActiveRows.Any(),
                    [.. v.ActiveRows.OrderBy(r => r.Component, StringComparer.Ordinal).Select(r => new GasComponentValue(r.Component, r.MolPct))],
                    [.. GasCompositionRules.LocksOf(GasCompositionRules.AffectedByEdit(v.Slot), locked).Select(p => new GasLockDto(p.Label, p.Reason))]))]))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GasStreamDetailDto(
            stream.Id, stream.StreamId, stream.Number, stream.Name ?? string.Empty, stream.IsOnshore, stream.IsSour, stream.IsHmb,
            stream.HmbDoc, stream.Group, stream.Hidden, cases, canEdit);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Склад газу змінено й закомічено, але перерахунок залежних (довідник {RegistryId}, рік {Year}) не поставлено; перерахуйте вручну.")]
    private static partial void LogEnqueueFailed(ILogger logger, Exception exception, int registryId, int year);
}
