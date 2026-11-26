// src/Ecr.Infrastructure/Persistence/GasCompositionStore.cs
using Ecr.Application.GasComposition;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Flert;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IGasCompositionStore"/> над довідниками складу газу (D-NEW).</summary>
/// <param name="db">Контекст.</param>
/// <remarks>
/// ⚠ Читає довідники напряму (поля за кодом), а не через загальний перелік записів: сторінці потрібні три рівні разом
/// (потік → версії → рядки складу), і пакетне читання тут — три запити на всю базу (523 потоки, ~1 000 версій).
/// </remarks>
public sealed class GasCompositionStore(EcrDbContext db) : IGasCompositionStore
{
    /// <inheritdoc />
    public async Task<GasRegistryIds?> FindRegistriesAsync(CancellationToken ct)
    {
        var codes = new[]
        {
            FlertCompositionSchema.StreamRegistry, FlertCompositionSchema.CaseRegistry,
            FlertCompositionSchema.CompositionRegistry, FlertCompositionSchema.ComponentRegistry,
        };
        var found = await db.RegistryDefs.AsNoTracking()
            .Where(d => codes.Contains(d.Code))
            .Select(d => new { d.Code, d.Id })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byCode = found.ToDictionary(d => d.Code, d => d.Id, StringComparer.OrdinalIgnoreCase);

        return codes.All(byCode.ContainsKey)
            ? new GasRegistryIds(
                byCode[FlertCompositionSchema.StreamRegistry],
                byCode[FlertCompositionSchema.CaseRegistry],
                byCode[FlertCompositionSchema.CompositionRegistry],
                byCode[FlertCompositionSchema.ComponentRegistry])
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GasStreamRecord>> ListStreamsAsync(GasRegistryIds ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var fields = await FieldsAsync(ids.Stream, ct).ConfigureAwait(false);
        var entries = await db.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == ids.Stream && !e.IsDeleted)
            .Select(e => e.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var values = await ValuesAsync(ids.Stream, ct).ConfigureAwait(false);

        return [.. entries.Select(id =>
        {
            var v = new Row(values, fields, id);
            return new GasStreamRecord(
                id,
                (int)(v.Number(FlertCompositionSchema.StreamLegacyId) ?? 0),
                v.Text(FlertCompositionSchema.StreamNumber),
                v.Text(FlertCompositionSchema.StreamName),
                v.Bool(FlertCompositionSchema.StreamIsOnshore) ?? false,
                v.Bool(FlertCompositionSchema.StreamIsSour) ?? false,
                v.Bool(FlertCompositionSchema.StreamIsHmb) ?? false,
                v.Text(FlertCompositionSchema.StreamHmbDoc),
                v.Number(FlertCompositionSchema.StreamGroup) is { } g ? (int)g : null,
                v.Bool(FlertCompositionSchema.StreamExcludeInList) ?? false);
        })];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GasVersionRecord>> ListVersionsAsync(GasRegistryIds ids, long? streamEntryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var caseFields = await FieldsAsync(ids.Case, ct).ConfigureAwait(false);
        var rowFields = await FieldsAsync(ids.Composition, ct).ConfigureAwait(false);
        var streamField = caseFields.GetValueOrDefault(FlertCompositionSchema.CaseStream);

        var cases = db.RegistryEntries.AsNoTracking().Where(e => e.RegistryDefId == ids.Case && !e.IsDeleted);
        if (streamEntryId is { } streamId)
        {
            cases = cases.Where(e => db.RegistryValues.Any(v =>
                v.RegistryEntryId == e.Id && v.RegistryFieldDefId == streamField && v.ValueRefEntryId == streamId));
        }

        var entries = await cases
            .Select(e => new { e.Id, e.Code, e.ValidFrom, e.ValidTo })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var caseIds = entries.Select(e => e.Id).ToList();

        var caseValues = await (
                from v in db.RegistryValues.AsNoTracking()
                join e in cases on v.RegistryEntryId equals e.Id
                where e.RegistryDefId == ids.Case
                select v)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var caseLookup = caseValues.ToLookup(v => v.RegistryEntryId);

        var caseField = rowFields.GetValueOrDefault(FlertCompositionSchema.RowCase);
        var componentField = rowFields.GetValueOrDefault(FlertCompositionSchema.RowComponent);
        var molField = rowFields.GetValueOrDefault(FlertCompositionSchema.RowMolPct);
        var rows = await (
                from row in db.RegistryEntries.AsNoTracking()
                where row.RegistryDefId == ids.Composition && !row.IsDeleted
                join caseValue in db.RegistryValues.AsNoTracking() on row.Id equals caseValue.RegistryEntryId
                where caseValue.RegistryFieldDefId == caseField
                join parent in cases on caseValue.ValueRefEntryId equals parent.Id
                join componentValue in db.RegistryValues.AsNoTracking() on row.Id equals componentValue.RegistryEntryId
                where componentValue.RegistryFieldDefId == componentField
                join component in db.RegistryEntries.AsNoTracking() on componentValue.ValueRefEntryId equals component.Id
                join molValue in db.RegistryValues.AsNoTracking() on row.Id equals molValue.RegistryEntryId
                where molValue.RegistryFieldDefId == molField
                select new
                {
                    CaseId = parent.Id,
                    row.Id,
                    row.Code,
                    Component = component.Code,
                    MolPct = molValue.ValueNumeric ?? 0m,
                    row.IsActive,
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var rowLookup = rows.ToLookup(r => r.CaseId);

        return [.. entries.Select(e =>
        {
            var v = new Row(caseLookup[e.Id], caseFields);
            return new GasVersionRecord(
                e.Id,
                e.Code,
                (long)(v.Ref(FlertCompositionSchema.CaseStream) ?? 0),
                v.Text(FlertCompositionSchema.CaseName) ?? e.Code,
                e.ValidFrom,
                e.ValidTo,
                v.Number(FlertCompositionSchema.CaseTemperature),
                v.Number(FlertCompositionSchema.CaseDensityOp),
                v.Number(FlertCompositionSchema.CaseMwHysys),
                v.Number(FlertCompositionSchema.CaseZ),
                v.Text(FlertCompositionSchema.CaseRuleParse),
                v.Number(FlertCompositionSchema.CaseLegacyTypeId) is { } t ? (int)t : null,
                v.Number(FlertCompositionSchema.CaseLegacyCompositionId) is { } c ? (int)c : null,
                v.Text(FlertCompositionSchema.CaseFlertAliases),
                [.. rowLookup[e.Id].Select(r => new GasRowRecord(r.Id, r.Code, r.Component, r.MolPct, r.IsActive))]);
        })];
    }

    /// <summary>
    /// Назва компонента за мовами — лише справжня: імпорт заводить запис із назвою, рівною коду FLERT (<c>MMercaptan</c>), і
    /// така «назва» нічого не каже; її відкидаємо, щоб клієнт показав типову з каталогу рядків.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NamesOf(string code, LocalizedText? display)
        => (display?.Values ?? new Dictionary<string, string>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Value) && !string.Equals(p.Value.Trim(), code, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GasComponentRecord>> ListComponentsAsync(GasRegistryIds ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var fields = await FieldsAsync(ids.Component, ct).ConfigureAwait(false);
        var entries = await db.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == ids.Component && !e.IsDeleted && e.IsActive)
            .Select(e => new { e.Id, e.Code, e.DisplayL10n, e.Ordinal })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var values = await ValuesAsync(ids.Component, ct).ConfigureAwait(false);

        return [.. entries.Select(e =>
        {
            var v = new Row(values, fields, e.Id);
            return new GasComponentRecord(e.Id, e.Code, NamesOf(e.Code, e.DisplayL10n), v.Text("FORMULA"), v.Number("MW"), e.Ordinal);
        })];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Той самий зв'язок «документ → довідник», що <see cref="RegistryImpactStore"/>: актуальний прогін методології, версія
    /// якої читає довідник (<c>cfg.RegistryUse</c>, <c>SourceKind = 1</c>). Замкнений — період <c>Closed</c> або аркуш
    /// оригіналу (<c>SheetCopyId = null</c>) у стані <c>Approved</c> того самого документа й періоду.
    /// </remarks>
    public Task<IReadOnlyList<GasLockedPeriod>> ListLockedPeriodsAsync(GasRegistryIds ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return LockedPeriodsAsync([.. ids.All], ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GasLockedPeriod>> ListLockedPeriodsAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryDefIds);
        return LockedPeriodsAsync([.. registryDefIds.Distinct()], ct);
    }

    /// <summary>
    /// Замкнені періоди за довідниками. ✎ D4 (REVIEW-sg-flaring): вікно періоду — з <c>doc.Period.PeriodStart/PeriodEnd</c>,
    /// а не з арифметики ключа (квартальний і довільний ключі не <c>yyyyMM</c>/<c>yyyy00</c>).
    /// </summary>
    private async Task<IReadOnlyList<GasLockedPeriod>> LockedPeriodsAsync(List<int> registryIds, CancellationToken ct)
    {
        if (registryIds.Count == 0)
        {
            return [];
        }

        var used =
            from use in db.RegistryUses.AsNoTracking()
            where use.SourceKind == RegistryUse.MethodologyVersionSource && registryIds.Contains(use.RegistryDefId)
            join result in db.CalculationResults.AsNoTracking() on use.SourceId equals result.MethodologyVersionId
            join run in db.CalculationRuns.AsNoTracking() on result.CalculationRunId equals run.Id
            where run.Status == CalculationRun.CurrentStatus
            join document in db.Documents.AsNoTracking() on result.DocumentId equals document.Id
            join period in db.Periods.AsNoTracking()
                on new { document.ProjectId, result.PeriodKey } equals new { period.ProjectId, PeriodKey = period.PeriodKeyValue }
            select new
            {
                result.DocumentId,
                result.PeriodKey,
                period.PeriodStart,
                period.PeriodEnd,
                Closed = period.State == PeriodState.Closed,
                Approved = db.ApprovalStates.Any(s => s.DocumentId == result.DocumentId && s.PeriodKey == result.PeriodKey
                                                       && s.SheetCopyId == null && s.Status == DocumentStatus.Approved),
            };

        var rows = await used
            .Where(r => r.Closed || r.Approved)
            .Select(r => new { r.PeriodKey, r.PeriodStart, r.PeriodEnd, r.Closed })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows
            .GroupBy(r => (r.PeriodKey, r.PeriodStart, r.PeriodEnd))
            .Select(g => new GasLockedPeriod(
                g.Key.PeriodKey,
                g.Any(r => r.Closed) ? GasLockedPeriod.Closed : GasLockedPeriod.Approved,
                g.Key.PeriodStart,
                g.Key.PeriodEnd))
            .OrderBy(p => p.PeriodKey)
            .ThenBy(p => p.Start)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> ListDependentYearsAsync(GasRegistryIds ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var registryIds = ids.All.ToList();

        var rows = await (
                from use in db.RegistryUses.AsNoTracking()
                where use.SourceKind == RegistryUse.MethodologyVersionSource && registryIds.Contains(use.RegistryDefId)
                join result in db.CalculationResults.AsNoTracking() on use.SourceId equals result.MethodologyVersionId
                join run in db.CalculationRuns.AsNoTracking() on result.CalculationRunId equals run.Id
                where run.Status == CalculationRun.CurrentStatus
                join document in db.Documents.AsNoTracking() on result.DocumentId equals document.Id
                join period in db.Periods.AsNoTracking()
                    on new { document.ProjectId, result.PeriodKey } equals new { period.ProjectId, PeriodKey = period.PeriodKeyValue }
                where period.State == PeriodState.Open || period.State == PeriodState.Grace
                select new { use.RegistryDefId, result.PeriodKey })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.RegistryDefId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(r => r.PeriodKey / 100).Distinct().Order()]);
    }

    /// <inheritdoc />
    public async Task<int> MaxStreamIdAsync(GasRegistryIds ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var fields = await FieldsAsync(ids.Stream, ct).ConfigureAwait(false);
        var field = fields.GetValueOrDefault(FlertCompositionSchema.StreamLegacyId);
        var max = await (
                from v in db.RegistryValues.AsNoTracking()
                where v.RegistryFieldDefId == field
                join e in db.RegistryEntries.AsNoTracking() on v.RegistryEntryId equals e.Id
                where e.RegistryDefId == ids.Stream
                select v.ValueNumeric)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        return (int)(max ?? 0m);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListCodesAsync(int registryDefId, string prefix, CancellationToken ct)
        => await db.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == registryDefId && e.Code.StartsWith(prefix))
            .Select(e => e.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private async Task<Dictionary<string, int>> FieldsAsync(int registryDefId, CancellationToken ct)
        => (await db.RegistryFieldDefs.AsNoTracking()
                .Where(f => f.RegistryDefId == registryDefId)
                .Select(f => new { f.Code, f.Id })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(f => f.Code, f => f.Id, StringComparer.Ordinal);

    private async Task<ILookup<long, Ecr.Domain.Entities.Dictionaries.RegistryValue>> ValuesAsync(int registryDefId, CancellationToken ct)
        => (await (
                    from v in db.RegistryValues.AsNoTracking()
                    join e in db.RegistryEntries.AsNoTracking() on v.RegistryEntryId equals e.Id
                    where e.RegistryDefId == registryDefId && !e.IsDeleted
                    select v)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToLookup(v => v.RegistryEntryId);

    /// <summary>Значення одного запису за кодом поля.</summary>
    private sealed class Row
    {
        private readonly Dictionary<int, Ecr.Domain.Entities.Dictionaries.RegistryValue> _byField;
        private readonly Dictionary<string, int> _fields;

        public Row(IEnumerable<Ecr.Domain.Entities.Dictionaries.RegistryValue> values, Dictionary<string, int> fields)
        {
            _byField = values.GroupBy(v => v.RegistryFieldDefId).ToDictionary(g => g.Key, g => g.First());
            _fields = fields;
        }

        public Row(ILookup<long, Ecr.Domain.Entities.Dictionaries.RegistryValue> values, Dictionary<string, int> fields, long entryId)
            : this(values[entryId], fields)
        {
        }

        private Ecr.Domain.Entities.Dictionaries.RegistryValue? Of(string code)
            => _fields.TryGetValue(code, out var id) ? _byField.GetValueOrDefault(id) : null;

        public string? Text(string code) => Of(code)?.ValueString;

        public decimal? Number(string code) => Of(code)?.ValueNumeric;

        public bool? Bool(string code) => Of(code)?.ValueBool;

        public long? Ref(string code) => Of(code)?.ValueRefEntryId;
    }
}
