// src/Ecr.Application/Registries/Flert/FlertCompositionMapping.cs
using System.Globalization;
using Ecr.Application.Ports;

namespace Ecr.Application.Registries.Flert;

/// <summary>Рівень зауваження зіставлення.</summary>
public enum FlertIssueSeverity
{
    /// <summary>Записано, але людині варто подивитися (дубль, конфлікт псевдонімів, кейс без складу).</summary>
    Warning = 0,

    /// <summary>Знімок не застосовується зовсім: копія лишається попередньою.</summary>
    Error = 1,
}

/// <summary>Зауваження зіставлення — для журналу синку.</summary>
/// <param name="Severity">Рівень.</param>
/// <param name="Kind">Вид (сталі <see cref="FlertCompositionMapping"/>).</param>
/// <param name="Subject">Чого стосується: колонка, код кейсу, Id потоку.</param>
/// <param name="Detail">Подробиці для людини.</param>
public sealed record FlertMappingIssue(FlertIssueSeverity Severity, string Kind, string Subject, string Detail);

/// <summary>Запис <c>STREAM</c>: один на <c>Stream_ID</c>.</summary>
/// <param name="Code">Код запису: <c>S{Stream_ID}</c> (§11.4).</param>
/// <param name="LegacyId">Id потоку FLERT.</param>
/// <param name="Number">Номер (не унікальний).</param>
/// <param name="Name">Назва.</param>
/// <param name="IsOnshore">Суша.</param>
/// <param name="IsSour">Кислий.</param>
/// <param name="IsHmb">Потік HMB.</param>
/// <param name="HmbDoc">Документ HMB.</param>
/// <param name="Group">Група.</param>
/// <param name="ExcludeInList">Не показувати в списках.</param>
public sealed record FlertStreamEntry(
    string Code,
    int LegacyId,
    string? Number,
    string? Name,
    bool? IsOnshore,
    bool? IsSour,
    bool? IsHmb,
    string? HmbDoc,
    int? Group,
    bool? ExcludeInList);

/// <summary>Запис <c>STREAM_CASE</c>: одна версія складу кейсу з вікном дії.</summary>
/// <param name="Code">Код запису: <c>C{StreamType_ID}</c> + <c>_yyyyMMdd</c> початку вікна (коли він є).</param>
/// <param name="StreamCode">Код потоку.</param>
/// <param name="CaseName">Назва кейсу.</param>
/// <param name="ValidFrom">Початок вікна, включно (UTC-дата); <c>null</c> — відкрите.</param>
/// <param name="ValidTo">Кінець вікна, виключно (UTC-дата); <c>null</c> — чинна версія.</param>
/// <param name="TemperatureC">Температура, °C.</param>
/// <param name="DensityOp">Робоча густина HYSYS; 0 у FLERT → <c>null</c>.</param>
/// <param name="MwHysys">Молярна маса HYSYS; 0 у FLERT → <c>null</c>.</param>
/// <param name="Z">Стисливість; 0 у FLERT → <c>null</c>.</param>
/// <param name="RuleParse"><c>RuleParse</c> FLERT.</param>
/// <param name="LegacyTypeId">Id <c>FlaringStreamType</c>.</param>
/// <param name="LegacyCompositionId">Id <c>FlaringStreamGasComposition</c>.</param>
/// <param name="NoComposition">Σ = 0: складу немає (позначка, не нулі).</param>
/// <param name="MolPctSum">Σ мол.% записаних рядків складу.</param>
public sealed record FlertCaseEntry(
    string Code,
    string StreamCode,
    string CaseName,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    decimal? TemperatureC,
    decimal? DensityOp,
    decimal? MwHysys,
    decimal? Z,
    string? RuleParse,
    int LegacyTypeId,
    int? LegacyCompositionId,
    bool NoComposition,
    decimal MolPctSum)
{
    /// <summary>Чи діє версія на дату <paramref name="on"/> (вікно <c>[ValidFrom, ValidTo)</c>).</summary>
    /// <param name="on">Дата.</param>
    public bool IsValidOn(DateOnly on) => (ValidFrom is not { } from || from <= on) && (ValidTo is not { } to || on < to);
}

/// <summary>Запис <c>GAS_COMPOSITION</c>: компонент кейсу з ненульовою мол.%.</summary>
/// <param name="Code">Код запису: <c>{код кейсу}_{компонент}</c>.</param>
/// <param name="CaseCode">Код кейсу.</param>
/// <param name="Component">Код компонента (<see cref="FlertCompositionSchema.Components"/>).</param>
/// <param name="MolPct">Мол.%.</param>
public sealed record FlertCompositionEntry(string Code, string CaseCode, string Component, decimal MolPct);

/// <summary>Результат зіставлення знімка FLERT.</summary>
/// <param name="Streams">Потоки за кодом.</param>
/// <param name="Cases">Версії кейсів за кодом.</param>
/// <param name="Rows">Рядки складу за кодом.</param>
/// <param name="Issues">Зауваження.</param>
/// <param name="SourceRows">Скільки рядків прочитано з FLERT.</param>
public sealed record FlertCompositionMirror(
    IReadOnlyList<FlertStreamEntry> Streams,
    IReadOnlyList<FlertCaseEntry> Cases,
    IReadOnlyList<FlertCompositionEntry> Rows,
    IReadOnlyList<FlertMappingIssue> Issues,
    int SourceRows)
{
    /// <summary>Є зауваження рівня <see cref="FlertIssueSeverity.Error"/> — знімок не застосовується.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == FlertIssueSeverity.Error);
}

/// <summary>Результат пошуку кейсу за номером потоку.</summary>
public enum FlertCaseMatchStatus
{
    /// <summary>Рівно одна версія.</summary>
    Found = 0,

    /// <summary>Жодної.</summary>
    NotFound = 1,

    /// <summary>Більше однієї — ніколи не «перша ліпша» (<c>FirstOrDefault</c> старої системи).</summary>
    Ambiguous = 2,
}

/// <summary>Знайдений кейс або причина, чому його немає.</summary>
/// <param name="Status">Результат.</param>
/// <param name="Case">Кейс, коли <see cref="FlertCaseMatchStatus.Found"/>.</param>
/// <param name="Candidates">Коди всіх кандидатів (для тексту проблеми при неоднозначності).</param>
public sealed record FlertCaseMatch(FlertCaseMatchStatus Status, FlertCaseEntry? Case, IReadOnlyList<string> Candidates);

/// <summary>
/// Чисте зіставлення знімка <c>FLERT.dbo.usp_GetAllStreamDataFlattened</c> із записами дзеркала
/// <c>STREAM</c> → <c>STREAM_CASE</c> → <c>GAS_COMPOSITION</c> (<c>D-476</c>, план FLERT F1).
/// </summary>
/// <remarks>
/// Правила (людина 11.11 через координатора):
/// <list type="bullet">
/// <item><b>NULL або 0 компонента</b> — рядка складу немає (у <c>REGSUM</c> це 0, як «NULL → 0» старої системи,
/// але видно як відсутній рядок).</item>
/// <item><b>Σ = 0</b> (кейси «No composition») — кейс БЕЗ складу з позначкою <see cref="FlertCompositionSchema.CaseNoComposition"/>
/// і зауваженням <see cref="NoComposition"/>: далі це проблема розрахунку, ніколи не нульові викиди й не падіння.</item>
/// <item><b>Пари дублів</b> <c>CO</c>/<c>Carbon_Monoxide</c>, <c>H2</c>/<c>Hydrogen</c>, <c>O2</c>/<c>Oxygen</c> — виграє КОРОТКА
/// назва; обидві заповнені різними значеннями — зауваження <see cref="AliasConflict"/>. Заповнена лише довга — береться вона.</item>
/// <item><b>Вікно дії</b> — вікно складу (<c>GasComposition_ValidFrom/To</c>, мітки FLERT — UTC): початок — дата (вниз),
/// кінець — виключна дата (вгору, якщо є час доби). Порожнє вікно — версію пропущено із зауваженням.</item>
/// <item><b>Дрейф схеми</b>: невідома колонка — <see cref="FlertIssueSeverity.Warning"/>, відсутня колонка зіставлення —
/// <see cref="FlertIssueSeverity.Error"/> (знімок не застосовується).</item>
/// <item><b>Дублі номерів потоків</b> (15 на копії 11.11) — ключ пошуку кейсу містить «суша/море» і кейс;
/// неоднозначність — <see cref="FlertCaseMatchStatus.Ambiguous"/>, а не перший-ліпший (<see cref="FindByNumber"/>).</item>
/// </list>
/// </remarks>
public static class FlertCompositionMapping
{
    /// <summary>Колонка, якої зіставлення не знає.</summary>
    public const string UnknownColumn = "unknownColumn";

    /// <summary>Немає колонки, яку зіставлення читає.</summary>
    public const string MissingColumn = "missingColumn";

    /// <summary>Обидві назви пари дублів заповнені різними значеннями.</summary>
    public const string AliasConflict = "aliasConflict";

    /// <summary>Σ складу = 0: кейс без складу.</summary>
    public const string NoComposition = "noComposition";

    /// <summary>Дві версії кейсу починаються того самого дня.</summary>
    public const string DuplicateVersion = "duplicateVersion";

    /// <summary>Вікно дії порожнє (кінець не пізніше початку).</summary>
    public const string EmptyValidity = "emptyValidity";

    /// <summary>Рядок без Id потоку, Id кейсу або назви кейсу.</summary>
    public const string RowWithoutKey = "rowWithoutKey";

    /// <summary>Значення не читається як число, дата чи прапорець.</summary>
    public const string UnreadableValue = "unreadableValue";

    /// <summary>Зіставляє знімок.</summary>
    /// <param name="table">Результат запиту FLERT.</param>
    public static FlertCompositionMirror Map(ExternalTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var issues = new List<FlertMappingIssue>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++)
        {
            index.TryAdd(table.Columns[i], i);
        }

        var known = new HashSet<string>(
            FlertCompositionSchema.MappedColumns.Concat(FlertCompositionSchema.KnownUnusedColumns), StringComparer.OrdinalIgnoreCase);

        foreach (var column in table.Columns.Where(c => !known.Contains(c)))
        {
            issues.Add(new(FlertIssueSeverity.Warning, UnknownColumn, column, $"Колонка «{column}» FLERT не зіставлена — ігнорується."));
        }

        foreach (var column in FlertCompositionSchema.MappedColumns.Where(c => !index.ContainsKey(c)))
        {
            issues.Add(new(FlertIssueSeverity.Error, MissingColumn, column, $"У результаті FLERT немає колонки «{column}»: схема змінилася."));
        }

        if (issues.Any(i => i.Severity == FlertIssueSeverity.Error))
        {
            return new FlertCompositionMirror([], [], [], issues, table.Rows.Count);
        }

        var reader = new RowReader(index, issues);
        var streams = new Dictionary<int, (FlertStreamEntry Entry, bool IsCurrent)>();
        var cases = new Dictionary<string, (FlertCaseEntry Entry, DateTime? From, List<FlertCompositionEntry> Rows)>(StringComparer.Ordinal);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            reader.Row = row;
            reader.RowNumber = r + 1;

            var streamId = reader.Int("Stream_ID");
            var typeId = reader.Int("StreamType_ID");
            var caseName = reader.Text("StreamType")?.Trim();
            if (streamId is not { } sid || typeId is not { } tid || string.IsNullOrEmpty(caseName))
            {
                issues.Add(new(FlertIssueSeverity.Warning, RowWithoutKey, Row(r + 1),
                    $"Рядок {r + 1}: немає Stream_ID, StreamType_ID або StreamType — пропущено."));
                continue;
            }

            var validFrom = reader.Moment("GasComposition_ValidFrom");
            var validTo = reader.Moment("GasComposition_ValidTo");
            var isCurrent = validTo is null;

            var stream = new FlertStreamEntry(
                StreamCode(sid),
                sid,
                reader.Text("StreamNumber")?.Trim() is { Length: > 0 } number ? number : null,
                reader.Text("Stream_Name")?.Trim(),
                reader.Bool("isOnshore"),
                reader.Bool("isSour"),
                reader.Bool("isHMB"),
                reader.Text("streamHMB")?.Trim() is { Length: > 0 } hmb ? hmb : null,
                reader.Int("StreamGroup"),
                reader.Bool("excludeInListBox"));

            // Атрибути потоку — з чинної версії, якщо вона є (історичні рядки несуть ті самі або старіші).
            if (!streams.TryGetValue(sid, out var seen) || (isCurrent && !seen.IsCurrent))
            {
                streams[sid] = (stream, isCurrent);
            }

            var from = validFrom is { } vf ? DateOnly.FromDateTime(vf) : (DateOnly?)null;
            var to = validTo is { } vt ? Ceiling(vt) : (DateOnly?)null;
            if (from is { } f0 && to is { } t0 && t0 <= f0)
            {
                issues.Add(new(FlertIssueSeverity.Warning, EmptyValidity, CaseCode(tid, from),
                    $"Версія кейсу «{caseName}» потоку {sid}: вікно [{Iso(validFrom)}, {Iso(validTo)}) не дає жодного дня — пропущено."));
                continue;
            }

            var code = CaseCode(tid, from);
            var rows = Components(reader, code, issues);
            var sum = rows.Sum(x => x.MolPct);

            var entry = new FlertCaseEntry(
                code,
                StreamCode(sid),
                caseName,
                from,
                to,
                reader.Decimal("Temperature"),
                NullIfZero(reader.Decimal("Density")),
                NullIfZero(reader.Decimal("Molecular_Weight")),
                NullIfZero(reader.Decimal("Z_CompressibilityFactor")),
                reader.Text("RuleParse")?.Trim() is { Length: > 0 } rule ? rule : null,
                tid,
                reader.Int("GasComposition_ID"),
                NoComposition: rows.Count == 0,
                MolPctSum: sum);

            if (cases.TryGetValue(code, out var existing))
            {
                // Дві версії, що починаються того самого дня: копія тримає пізнішу за міткою FLERT.
                var keepNew = (validFrom ?? DateTime.MinValue) > (existing.From ?? DateTime.MinValue);
                issues.Add(new(FlertIssueSeverity.Warning, DuplicateVersion, code,
                    $"Дві версії кейсу «{caseName}» потоку {sid} починаються {Iso(validFrom)}: лишається {(keepNew ? "новіша" : "перша")}."));
                if (!keepNew)
                {
                    continue;
                }
            }

            cases[code] = (entry, validFrom, rows);
        }

        foreach (var (code, (entry, _, _)) in cases.Where(c => c.Value.Entry.NoComposition).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            issues.Add(new(FlertIssueSeverity.Warning, NoComposition, code,
                $"Кейс «{entry.CaseName}» потоку {entry.StreamCode}: Σ складу = 0 — записано без складу, розрахунок на ньому дасть проблему, а не нуль."));
        }

        return new FlertCompositionMirror(
            [.. streams.Values.Select(s => s.Entry).OrderBy(s => s.Code, StringComparer.Ordinal)],
            [.. cases.Values.Select(c => c.Entry).OrderBy(c => c.Code, StringComparer.Ordinal)],
            [.. cases.Values.SelectMany(c => c.Rows).OrderBy(x => x.Code, StringComparer.Ordinal)],
            issues,
            table.Rows.Count);
    }

    /// <summary>
    /// Кейс потоку за НОМЕРОМ (TOx / Continuous SG старої системи: <c>StreamNumber == …</c> і <c>StreamType.Contains(…)</c>),
    /// з «суша/море» в ключі й чинний на дату. Дублі номерів не вирішуються першим-ліпшим.
    /// </summary>
    /// <param name="mirror">Дзеркало.</param>
    /// <param name="number">Номер потоку.</param>
    /// <param name="isOnshore">Суша (<c>true</c>) чи море.</param>
    /// <param name="caseFragment">Фрагмент назви кейсу (<c>SOR</c>, <c>EOR</c>, <c>Winter</c>…), без урахування регістру.</param>
    /// <param name="on">Дата, на яку версія мусить діяти.</param>
    public static FlertCaseMatch FindByNumber(
        FlertCompositionMirror mirror, string number, bool isOnshore, string caseFragment, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseFragment);

        var streamCodes = mirror.Streams
            .Where(s => string.Equals(s.Number, number.Trim(), StringComparison.OrdinalIgnoreCase) && s.IsOnshore == isOnshore)
            .Select(s => s.Code)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = mirror.Cases
            .Where(c => streamCodes.Contains(c.StreamCode)
                        && c.CaseName.Contains(caseFragment, StringComparison.OrdinalIgnoreCase)
                        && c.IsValidOn(on))
            .ToList();

        return candidates.Count switch
        {
            0 => new FlertCaseMatch(FlertCaseMatchStatus.NotFound, null, []),
            1 => new FlertCaseMatch(FlertCaseMatchStatus.Found, candidates[0], [candidates[0].Code]),
            _ => new FlertCaseMatch(FlertCaseMatchStatus.Ambiguous, null, [.. candidates.Select(c => c.Code)]),
        };
    }

    /// <summary>Код запису потоку.</summary>
    /// <param name="streamId">Id потоку FLERT.</param>
    public static string StreamCode(int streamId) => "S" + streamId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Код версії кейсу.</summary>
    /// <param name="streamTypeId">Id кейсу FLERT.</param>
    /// <param name="validFrom">Початок вікна версії.</param>
    public static string CaseCode(int streamTypeId, DateOnly? validFrom)
        => "C" + streamTypeId.ToString(CultureInfo.InvariantCulture)
               + (validFrom is { } from ? "_" + from.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : string.Empty);

    /// <summary>Рядки складу версії: правило псевдонімів, NULL/0 — без рядка.</summary>
    private static List<FlertCompositionEntry> Components(RowReader reader, string caseCode, List<FlertMappingIssue> issues)
    {
        var rows = new List<FlertCompositionEntry>();

        foreach (var component in FlertCompositionSchema.Components)
        {
            var columns = FlertCompositionSchema.ComponentColumns.Where(c => c.Component == component).Select(c => c.Column).ToList();
            var shortValue = columns.Contains(component, StringComparer.Ordinal) ? Filled(reader.Decimal(component)) : null;
            var longColumn = columns.FirstOrDefault(c => !string.Equals(c, component, StringComparison.Ordinal));
            var longValue = longColumn is null ? null : Filled(reader.Decimal(longColumn));

            if (shortValue is not null && longValue is not null && shortValue != longValue)
            {
                issues.Add(new(FlertIssueSeverity.Warning, AliasConflict, $"{caseCode}.{component}",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Кейс {caseCode}: {component} = {shortValue}, {longColumn} = {longValue} — взято {component} (коротка назва).")));
            }

            if ((shortValue ?? longValue) is { } value)
            {
                rows.Add(new FlertCompositionEntry($"{caseCode}_{component}", caseCode, component, value));
            }
        }

        return rows;
    }

    private static decimal? Filled(decimal? value) => value is { } v && v != 0m ? v : null;

    private static decimal? NullIfZero(decimal? value) => value is { } v && v != 0m ? v : null;

    private static DateOnly Ceiling(DateTime moment)
    {
        var date = DateOnly.FromDateTime(moment);
        return moment.TimeOfDay == TimeSpan.Zero ? date : date.AddDays(1);
    }

    private static string Iso(DateTime? moment)
        => moment is { } m ? m.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    private static string Row(int number) => "row " + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Читання значень рядка за іменем колонки з приведенням типів SQL.</summary>
    private sealed class RowReader(Dictionary<string, int> index, List<FlertMappingIssue> issues)
    {
        public IReadOnlyList<object?> Row { get; set; } = [];

        public int RowNumber { get; set; }

        private object? Raw(string column)
            => index.TryGetValue(column, out var i) && i < Row.Count ? Row[i] : null;

        public string? Text(string column) => Raw(column) switch
        {
            null => null,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };

        public int? Int(string column) => Raw(column) switch
        {
            null => null,
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            short value => value,
            byte value => value,
            decimal value when value == decimal.Truncate(value) && value is >= int.MinValue and <= int.MaxValue => (int)value,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            var other => Unreadable<int?>(column, other),
        };

        public decimal? Decimal(string column) => Raw(column) switch
        {
            null => null,
            decimal value => value,
            double value when !double.IsNaN(value) && !double.IsInfinity(value)
                              && value is >= (double)decimal.MinValue and <= (double)decimal.MaxValue => (decimal)value,
            float value when !float.IsNaN(value) && !float.IsInfinity(value) => (decimal)value,
            int value => value,
            long value => value,
            short value => value,
            string text when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            var other => Unreadable<decimal?>(column, other),
        };

        public bool? Bool(string column) => Raw(column) switch
        {
            null => null,
            bool value => value,
            int value when value is 0 or 1 => value == 1,
            byte value when value is 0 or 1 => value == 1,
            string text when bool.TryParse(text, out var parsed) => parsed,
            var other => Unreadable<bool?>(column, other),
        };

        /// <summary>Мітка FLERT — UTC (<c>QUESTIONS-BUSINESS.md</c>): <c>datetime</c> без поясу вважається UTC.</summary>
        public DateTime? Moment(string column) => Raw(column) switch
        {
            null => null,
            DateTime value => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeOffset value => value.UtcDateTime,
            var other => Unreadable<DateTime?>(column, other),
        };

        private T Unreadable<T>(string column, object value)
        {
            issues.Add(new(FlertIssueSeverity.Error, UnreadableValue, $"{column}@{RowNumber.ToString(CultureInfo.InvariantCulture)}",
                $"Рядок {RowNumber.ToString(CultureInfo.InvariantCulture)}: «{column}» типу {value.GetType().Name} не читається — знімок не застосовано."));
            return default!;
        }
    }
}
