// src/Ecr.Infrastructure/Jobs/FlaringRegistryProvisioner.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Flert;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Коди довідників факелів кислого газу (тести підставляють свої, щоб не ділити спільну базу).</summary>
/// <param name="Location">Місця факела AF.</param>
/// <param name="Split">Розподіл LP-аналізаторів острова D.</param>
/// <param name="Pin">Закріплення стовпців ТОх / постійних суші.</param>
public sealed record FlaringRegistryCodes(string Location, string Split, string Pin)
{
    /// <summary>Коди DESIGN-sg-flaring §5.7 (їх же називає запобіжник <see cref="FlaringRegistryLock"/>).</summary>
    public static FlaringRegistryCodes Default { get; } = new("FLARE_LOCATION", "LP_SOUR_SPLIT", "TOX_GAS_STREAM");
}

/// <summary>Що зробила провізія довідників факелів.</summary>
/// <param name="Created">Коди довідників, створених цим викликом.</param>
/// <param name="Rows">Скільки записів записано в щойно створені довідники.</param>
/// <param name="Warnings">Закріплення без потоку чи кейсу (рядок порожній — скрипт відхилить, а не дасть 0).</param>
public sealed record FlaringRegistryReport(IReadOnlyList<string> Created, int Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// ✎ SGF-flaring (DESIGN-sg-flaring §5.7; рішення людини 25–26.11 H5/H7/H10): довідники <c>FLARE_LOCATION</c>,
/// <c>LP_SOUR_SPLIT</c>, <c>TOX_GAS_STREAM</c> заводить КОД, як і довідники складу (<see cref="FlertRegistryProvisioner"/>,
/// «сід фічі несе лише рядки інтерфейсу»). Міграції немає.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Ідемпотентно і НЕ руйнівно: наявний довідник не чіпається зовсім (його веде адміністратор, H7; зміни — під
/// запобіжником закритих місяців). Рядки пишуться лише в ПОРОЖНІЙ довідник (створений цим викликом або наявний без записів); помилка запису — відмова й відкат (огляд M4).</item>
/// <item><c>LP_SOUR_SPLIT</c>: слоти 1–3 → потоки FLERT 19/21/146, v1 2024 = значення AF 0.4285714/0.4285714/0.1428571,
/// v2 з 2025-01-01 = 0.4/0.4/0.2; кейси «370 Summer»/«370 Winter» (DESIGN §2.1, §2.3).</item>
/// <item><c>TOX_GAS_STREAM</c> (з 2022-01-01): стовпці 14/412/415/416/417/453 × обидва поїзди, 417 → потік 575 (H5); 481:
/// Train 1 → 574 (481A), Train 2 → 573 (481B) (H10, L6 виправлено); рядок <c>CONT_ONSHORE</c>/<c>ALL</c> → потік 317 (416).
/// Назва кейсу SOR/EOR — ЄДИНА назва кейсу потоку з «SOR»/«EOR»; немає чи кілька — поле порожнє й попередження (скрипт
/// такий рядок відхилить: кейс не знайдено).</item>
/// <item>Потік — за <c>STREAM.LEGACY_ID</c> (Id FLERT); немає потоку — поле порожнє й попередження.</item>
/// </list>
/// Виклик — усередині транзакції імпорту складу (<see cref="FlertCompositionSyncJob"/>), автор — <c>svc-integration</c>.
/// </remarks>
public sealed class FlaringRegistryProvisioner(
    EcrDbContext db,
    IUnitOfWork uow,
    IClock clock,
    IAuditWriter audit,
    Application.Common.ICurrentUser currentUser,
    FlertRegistryProvisioner flert,
    RegistryEntryWriter writer)
{
    /// <summary>Операція рядка <c>aud.StructureChange</c> створеного довідника.</summary>
    public const string ProvisionOperation = "ProvisionFlaring";

    /// <summary>Місця факела AF: код, текст AF (ValueMap), суша, TypeReportID, установка.</summary>
    public static IReadOnlyList<(string Code, string AfName, bool Onshore, int TypeReport, string Unit)> Locations { get; } =
    [
        ("ONSHORE_HP", "Onshore - HP Flare", true, 300, "HP"),
        ("ONSHORE_LP", "Onshore - LP Flare", true, 300, "LP"),
        ("A_ISLAND_HP", "A Island - HP Flare", false, 301, "HP"),
        ("D_ISLAND_HP", "D Island - HP Flare", false, 302, "HP"),
        ("D_ISLAND_LP", "D Island - LP Flare", false, 302, "LP"),
    ];

    /// <summary>Слоти розподілу: слот, Id потоку FLERT, коефіцієнт v1 (2024, значення AF), v2 (з 2025).</summary>
    public static IReadOnlyList<(int Slot, int StreamId, decimal Coef2024, decimal Coef2025)> Slots { get; } =
    [
        (1, 19, 0.4285714m, 0.4m),
        (2, 21, 0.4285714m, 0.4m),
        (3, 146, 0.1428571m, 0.2m),
    ];

    /// <summary>Закріплення: стовпець, поїзд, Id потоку FLERT.</summary>
    public static IReadOnlyList<(string Column, string Train, int StreamId)> Pins { get; } = BuildPins();

    private static readonly (string Code, string En, string Ru, CellDataType Type, bool Required)[] LocationFields =
    [
        ("AF_NAME", "AF location text", "Текст места AF", CellDataType.String, true),
        ("IS_ONSHORE", "Onshore", "Суша", CellDataType.Bool, true),
        ("TYPE_REPORT", "Report type (300/301/302)", "Тип отчёта (300/301/302)", CellDataType.Int, true),
        ("FLARE_UNIT", "Flare unit (HP/LP)", "Факел (HP/LP)", CellDataType.String, false),
    ];

    private static readonly (string Code, string En, string Ru, CellDataType Type, bool Required)[] SplitFields =
    [
        ("SLOT", "Slot", "Слот", CellDataType.Int, true),
        ("STREAM", "Stream", "Поток", CellDataType.Lookup, false),
        ("COEF", "Coefficient", "Коэффициент", CellDataType.Decimal, true),
        ("CASE_SUMMER", "Summer case (Apr–Sep)", "Летний кейс (апр–сен)", CellDataType.String, false),
        ("CASE_WINTER", "Winter case", "Зимний кейс", CellDataType.String, false),
    ];

    private static readonly (string Code, string En, string Ru, CellDataType Type, bool Required)[] PinFields =
    [
        ("COLUMN", "Gas column", "Столбец газа", CellDataType.String, true),
        ("TRAIN", "Train", "Поезд", CellDataType.String, true),
        ("STREAM", "Stream", "Поток", CellDataType.Lookup, false),
        ("CASE_SOR", "SOR case", "Кейс SOR", CellDataType.String, false),
        ("CASE_EOR", "EOR case", "Кейс EOR", CellDataType.String, false),
    ];

    /// <summary>Заводить відсутні довідники факелів із рядками; наявні не чіпає.</summary>
    /// <param name="codes">Коди довідників факелів.</param>
    /// <param name="streamRegistryId">Довідник потоків складу (<c>STREAM</c>).</param>
    /// <param name="caseRegistryId">Довідник кейсів складу (<c>STREAM_CASE</c>).</param>
    /// <param name="ct">Скасування.</param>
    public async Task<FlaringRegistryReport> EnsureAsync(
        FlaringRegistryCodes codes, int streamRegistryId, int caseRegistryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var userId = currentUser.UserId
                     ?? throw new InvalidOperationException("Провізія довідників факелів без автора (svc-integration).");
        var created = new List<string>();
        var warnings = new List<string>();
        var rows = 0;

        var streams = await StreamsAsync(streamRegistryId, ct).ConfigureAwait(false);

        var location = await flert.FindAsync(codes.Location, ct).ConfigureAwait(false);
        if (location is null)
        {
            location = await flert.CreateAsync(codes.Location, "Flare locations (AF)", "Места факелов (AF)", isTemporal: false,
                RegistrySourceKind.Local, LocationFields, _ => null, userId, ct).ConfigureAwait(false);
            created.Add(location.Code);
        }

        if (await IsEmptyAsync(location, ct).ConfigureAwait(false))
        {
            var def = location;
            rows += await WriteAsync(def, [.. Locations.Select(l => new RegistryEntryWrite(l.Code, Values(
                ("AF_NAME", l.AfName), ("IS_ONSHORE", l.Onshore), ("TYPE_REPORT", l.TypeReport), ("FLARE_UNIT", l.Unit)))
            {
                DisplayName = l.AfName,
            })], warnings, ct).ConfigureAwait(false);
        }

        var split = await flert.FindAsync(codes.Split, ct).ConfigureAwait(false);
        if (split is null)
        {
            split = await flert.CreateAsync(codes.Split, "LP sour analyzers split (D Island)", "Распределение LP-анализаторов (остров D)",
                isTemporal: true, RegistrySourceKind.Local, SplitFields, f => f == "STREAM" ? (streamRegistryId, false) : null, userId, ct)
                .ConfigureAwait(false);
            await flert.AddKeyAsync(split, "PK", "Slot", isPrimary: true, ["SLOT"], userId, ct).ConfigureAwait(false);
            created.Add(split.Code);
        }

        if (await IsEmptyAsync(split, ct).ConfigureAwait(false))
        {
            var def = split;
            var writes = new List<RegistryEntryWrite>();
            foreach (var (slot, streamId, v1, v2) in Slots)
            {
                var stream = Stream(streams, streamId, $"{codes.Split} slot {slot}", warnings);
                foreach (var (suffix, coef, window) in (ReadOnlySpan<(string, decimal, ValidityWindow)>)
                         [("2024", v1, new ValidityWindow(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1))),
                          ("2025", v2, new ValidityWindow(new DateOnly(2025, 1, 1), null))])
                {
                    writes.Add(new RegistryEntryWrite(string.Create(CultureInfo.InvariantCulture, $"S{slot}_{suffix}"), Values(
                        ("SLOT", slot), ("STREAM", stream), ("COEF", coef), ("CASE_SUMMER", "370 Summer"), ("CASE_WINTER", "370 Winter")))
                    {
                        DisplayName = string.Create(CultureInfo.InvariantCulture, $"Slot {slot}: stream {streamId} × {coef}"),
                        Validity = window,
                    });
                }
            }

            rows += await WriteAsync(def, writes, warnings, ct).ConfigureAwait(false);
        }

        var pin = await flert.FindAsync(codes.Pin, ct).ConfigureAwait(false);
        if (pin is null)
        {
            pin = await flert.CreateAsync(codes.Pin, "Thermal oxidizer gas column → stream", "Столбец газа ТОх → поток",
                isTemporal: true, RegistrySourceKind.Local, PinFields, f => f == "STREAM" ? (streamRegistryId, false) : null, userId, ct)
                .ConfigureAwait(false);
            await flert.AddKeyAsync(pin, "PK", "Column + train", isPrimary: true, ["COLUMN", "TRAIN"], userId, ct).ConfigureAwait(false);
            created.Add(pin.Code);
        }

        if (await IsEmptyAsync(pin, ct).ConfigureAwait(false))
        {
            var def = pin;
            var caseNames = await CaseNamesAsync(caseRegistryId, ct).ConfigureAwait(false);
            var writes = new List<RegistryEntryWrite>();
            foreach (var (column, train, streamId) in Pins)
            {
                var label = $"{codes.Pin} {column}/{train}";
                var stream = Stream(streams, streamId, label, warnings);
                var names = stream is { } s && caseNames.TryGetValue(s, out var list) ? list : [];
                writes.Add(new RegistryEntryWrite(PinCode(column, train), Values(
                    ("COLUMN", column), ("TRAIN", train), ("STREAM", stream),
                    ("CASE_SOR", OnlyCase(names, "SOR", label, warnings)),
                    ("CASE_EOR", column == SgContOnshoreColumn ? null : OnlyCase(names, "EOR", label, warnings))))
                {
                    DisplayName = $"{column} · {train} → stream {streamId}",
                    Validity = new ValidityWindow(new DateOnly(2022, 1, 1), null),
                });
            }

            rows += await WriteAsync(def, writes, warnings, ct).ConfigureAwait(false);
        }

        foreach (var code in created)
        {
            var def = (await flert.FindAsync(code, ct).ConfigureAwait(false))!;
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, TemplateVersionId: 0, EntityType: "cfg.RegistryDef", EntityId: def.Id, ChangeClass.Guarded,
                    ProvisionOperation, OldJson: null,
                    NewJson: JsonSerializer.Serialize(new { registry = def.Code, isTemporal = def.IsTemporal }),
                    ChangeReason: "SGF-flaring (DESIGN-sg-flaring §5.7): довідник факелів заведено разом зі складом газу.",
                    userId, currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        return new FlaringRegistryReport(created, rows, warnings);
    }

    /// <summary>Стовпець закріплення постійних факелів суші.</summary>
    public const string SgContOnshoreColumn = "CONT_ONSHORE";

    /// <summary>Код запису закріплення (код запису — лише латиниця, цифри, підкреслення).</summary>
    /// <param name="column">Стовпець.</param>
    /// <param name="train">Поїзд.</param>
    public static string PinCode(string column, string train)
        => "P" + column + "_" + train.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static List<(string, string, int)> BuildPins()
    {
        var pins = new List<(string, string, int)>();
        foreach (var (column, stream) in (ReadOnlySpan<(string, int)>)[("14", 572), ("412", 313), ("415", 316), ("416", 317), ("417", 575), ("453", 361)])
        {
            pins.Add((column, "Train 1", stream));
            pins.Add((column, "Train 2", stream));
        }

        pins.Add(("481", "Train 1", 574));
        pins.Add(("481", "Train 2", 573));
        pins.Add((SgContOnshoreColumn, "ALL", 317));
        return pins;
    }

    private static Dictionary<string, object?> Values(params (string Field, object? Value)[] values)
        => values.ToDictionary(v => v.Field, v => v.Value, StringComparer.Ordinal);

    private static long? Stream(Dictionary<int, long> streams, int legacyId, string label, List<string> warnings)
    {
        if (streams.TryGetValue(legacyId, out var id))
        {
            return id;
        }

        warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: no STREAM with LEGACY_ID {legacyId} — stream left empty."));
        return null;
    }

    private static string? OnlyCase(IReadOnlyList<string> names, string type, string label, List<string> warnings)
    {
        var found = names.Where(n => n.Contains(type, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (found.Count == 1)
        {
            return found[0];
        }

        warnings.Add($"{label}: {found.Count} {type} cases ({string.Join(", ", found)}) — CASE_{type} left empty, set it by hand.");
        return null;
    }

    /// <summary>
    /// ✎ огляд M4: помилка запису рядка — відмова (транзакцію викликача відкочено), а не зауваження з напівзаповненим довідником.
    /// </summary>
    /// <exception cref="InvalidOperationException">Writer відхилив хоча б один рядок.</exception>
    private async Task<int> WriteAsync(RegistryDef def, List<RegistryEntryWrite> writes, List<string> warnings, CancellationToken ct)
    {
        _ = warnings;
        var result = await writer.WriteAsync(new RegistryEntryWriteBatch(def.Id, writes) { CreateOnly = true }, ct).ConfigureAwait(false);
        if (result.Errors.Count > 0 || !result.Applied)
        {
            throw new InvalidOperationException(
                $"SGF provisioning: {def.Code} rows rejected — " + string.Join("; ", result.Errors.Select(e => e.ToString())));
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return result.Added;
    }

    /// <summary>✎ огляд M4: рядки пишуться лише в ПОРОЖНІЙ довідник (щойно створений чи наявний без записів); наявні дані — ніколи.</summary>
    private async Task<bool> IsEmptyAsync(RegistryDef def, CancellationToken ct)
        => !await db.RegistryEntries.AsNoTracking().AnyAsync(e => e.RegistryDefId == def.Id, ct).ConfigureAwait(false);

    /// <summary>Id FLERT → запис <c>STREAM</c>.</summary>
    private async Task<Dictionary<int, long>> StreamsAsync(int streamRegistryId, CancellationToken ct)
    {
        var rows = await (
                from f in db.RegistryFieldDefs.AsNoTracking()
                where f.RegistryDefId == streamRegistryId && f.Code == FlertCompositionSchema.StreamLegacyId
                join v in db.RegistryValues.AsNoTracking() on f.Id equals v.RegistryFieldDefId
                join e in db.RegistryEntries.AsNoTracking() on v.RegistryEntryId equals e.Id
                where !e.IsDeleted && v.ValueNumeric != null
                select new { e.Id, v.ValueNumeric })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.GroupBy(r => (int)r.ValueNumeric!.Value).ToDictionary(g => g.Key, g => g.Min(r => r.Id));
    }

    /// <summary>Запис потоку → назви його кейсів (усі версії).</summary>
    private async Task<Dictionary<long, IReadOnlyList<string>>> CaseNamesAsync(int caseRegistryId, CancellationToken ct)
    {
        var fields = await db.RegistryFieldDefs.AsNoTracking()
            .Where(f => f.RegistryDefId == caseRegistryId
                        && (f.Code == FlertCompositionSchema.CaseStream || f.Code == FlertCompositionSchema.CaseName))
            .ToDictionaryAsync(f => f.Code, f => f.Id, ct)
            .ConfigureAwait(false);
        if (fields.Count < 2)
        {
            return [];
        }

        var streamField = fields[FlertCompositionSchema.CaseStream];
        var nameField = fields[FlertCompositionSchema.CaseName];
        var rows = await (
                from s in db.RegistryValues.AsNoTracking()
                where s.RegistryFieldDefId == streamField && s.ValueRefEntryId != null
                join n in db.RegistryValues.AsNoTracking() on s.RegistryEntryId equals n.RegistryEntryId
                where n.RegistryFieldDefId == nameField && n.ValueString != null
                join e in db.RegistryEntries.AsNoTracking() on s.RegistryEntryId equals e.Id
                where !e.IsDeleted
                select new { Stream = s.ValueRefEntryId!.Value, Name = n.ValueString! })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.GroupBy(r => r.Stream)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Name).Distinct(StringComparer.Ordinal)]);
    }
}
