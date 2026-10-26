using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Q-17: хто володіє коміркою таблиці «місяці в колонках» місячного проєкту
/// (<see cref="IMonthColumnRouter"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Прапор <c>Collection:MonthColumnRouting</c> (за замовчуванням <c>false</c>) читається ОДИН раз на життя
/// роутера (scope = запит/задача). Вимкнено — жоден метод не ходить до бази: <see cref="GetDocumentAsync"/> і
/// <see cref="ResolveInstancesAsync"/> повертають «не застосовується», а <see cref="ForeignColumnsAsync"/> — порожнє.
/// Це головна гарантія безпеки: до ввімкнення прапора поведінка сховища не відрізняється від попередньої.
/// </para>
/// <para>
/// ⚠ Кеші (документ, екземпляр, календар року) живуть у scope і НЕ інвалідуються: ідентичність екземпляра
/// незмінна, а календар року (рядки <c>doc.Period</c>) у межах одного запиту не змінюється. Значення комірок тут не
/// кешуються — кеш значень власників має інвалідуватися записом (комміт запису Q-17).
/// </para>
/// </remarks>
public sealed class MonthColumnRouter : IMonthColumnRouter
{
    /// <summary>Ключ прапора в конфігурації.</summary>
    public const string EnabledKey = "Collection:MonthColumnRouting";

    private readonly EcrDbContext _db;
    private readonly Dictionary<long, MonthRoutingDocument?> _documents = [];
    private readonly Dictionary<long, MonthRoutingInstance?> _instances = [];
    private readonly Dictionary<(int ProjectId, int Year), YearCalendar> _calendars = [];

    /// <summary>Створює роутер; прапор фіксується на життя об'єкта.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="configuration">Конфігурація (прапор <see cref="EnabledKey"/>).</param>
    public MonthColumnRouter(EcrDbContext db, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);

        _db = db;
        Enabled = configuration.GetValue("Collection:MonthColumnRouting", false);
    }

    /// <inheritdoc />
    public bool Enabled { get; }

    /// <inheritdoc />
    public bool AppliesToTable(TableDef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Enabled && !table.IsDeleted && table.LayoutKind == TableLayoutKind.MonthsInColumns;
    }

    /// <inheritdoc />
    public async Task<MonthRoutingDocument?> GetDocumentAsync(long documentId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        if (_documents.TryGetValue(documentId, out var cached))
        {
            return cached;
        }

        var found = await (
                from d in _db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in _db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                where p.PeriodKind == PeriodKind.Monthly
                select new MonthRoutingDocument(d.Id, p.Id, p.TemplateVersionId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        _documents[documentId] = found;
        return found;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, MonthRoutingInstance>> ResolveInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey? periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        var result = new Dictionary<long, MonthRoutingInstance>();
        if (!Enabled || tableInstanceIds.Count == 0)
        {
            return result;
        }

        var missing = tableInstanceIds.Distinct().Where(id => !_instances.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            Func<IQueryable<Domain.Entities.Documents.TableInstance>, IQueryable<InstanceRow>> shape = instances =>
                from instance in instances
                join document in _db.Documents.AsNoTracking() on instance.DocumentId equals document.Id
                join project in _db.Projects.AsNoTracking() on document.ProjectId equals project.Id
                where project.PeriodKind == PeriodKind.Monthly
                select new InstanceRow
                {
                    Id = instance.Id,
                    DocumentId = document.Id,
                    ProjectId = project.Id,
                    TemplateVersionId = project.TemplateVersionId,
                    TableDefId = instance.TableDefId,
                    PeriodKeyValue = instance.PeriodKeyValue,
                };

            // ⚠ Той самий вибір запиту, що й у NormalizedCellStore: період відомий — один seek у партиції;
            // невідомий один екземпляр — seek по періодах календаря; невідомий пакет — скан без ключа
            // (замір O3b: пакет на 90 Id через ключ дорожчий за скан).
            List<InstanceRow> rows;
            if (periodKey is { } known)
            {
                rows = await shape(RowStore.TableInstancesInPeriodQuery(_db, missing, known))
                    .ToListAsync(ct).ConfigureAwait(false);
            }
            else if (missing.Count == 1)
            {
                rows = await RowStore.FindTableInstancesAsync(_db, missing, shape, r => r.Id, ct).ConfigureAwait(false);
            }
            else
            {
                rows = await shape(_db.TableInstances.AsNoTracking().Where(t => missing.Contains(t.Id)))
                    .ToListAsync(ct).ConfigureAwait(false);
            }

            foreach (var id in missing)
            {
                _instances[id] = null;
            }

            foreach (var row in rows)
            {
                var document = new MonthRoutingDocument(row.DocumentId, row.ProjectId, row.TemplateVersionId);
                _documents.TryAdd(row.DocumentId, document);
                _instances[row.Id] = new MonthRoutingInstance(row.Id, document, row.TableDefId, new PeriodKey(row.PeriodKeyValue));
            }
        }

        foreach (var id in tableInstanceIds)
        {
            if (_instances.TryGetValue(id, out var instance) && instance is not null)
            {
                result[id] = instance;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PeriodKey?> AnchorOfAsync(int projectId, int year, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        var calendar = await CalendarAsync(projectId, year, ct).ConfigureAwait(false);
        return calendar.Anchor;
    }

    /// <inheritdoc />
    public async Task<PeriodKey> OwnerPeriodAsync(
        MonthRoutingDocument document, TableDef table, ColumnDef column, PeriodKey viewPeriod, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);

        if (!AppliesToTable(table))
        {
            return viewPeriod;
        }

        var calendar = await CalendarAsync(document.ProjectId, viewPeriod.Year, ct).ConfigureAwait(false);
        return OwnerOf(column, viewPeriod, calendar);
    }

    /// <inheritdoc />
    public async Task<bool> HasForeignOwnerAsync(
        MonthRoutingDocument document, TableDef table, ColumnDef column, PeriodKey viewPeriod, CancellationToken ct)
        => await OwnerPeriodAsync(document, table, column, viewPeriod, ct).ConfigureAwait(false) != viewPeriod;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ForeignColumn>> ForeignColumnsAsync(
        MonthRoutingDocument document, TableDef table, PeriodKey viewPeriod, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(table);

        if (!AppliesToTable(table))
        {
            return [];
        }

        var calendar = await CalendarAsync(document.ProjectId, viewPeriod.Year, ct).ConfigureAwait(false);
        if (!calendar.Keys.Contains(viewPeriod.Value))
        {
            // Перегляд поза календарем (немає рядка періоду, ECR-PRD-0422) — старе зберігання.
            return [];
        }

        var foreign = new List<ForeignColumn>();
        foreach (var column in table.Columns)
        {
            if (column.IsDeleted)
            {
                continue;
            }

            var owner = OwnerOf(column, viewPeriod, calendar);
            if (owner != viewPeriod)
            {
                foreign.Add(new ForeignColumn(column, owner));
            }
        }

        return foreign;
    }

    /// <summary>
    /// Власник введених комірок колонки для перегляду: чиста функція від календаря (див. <see cref="IMonthColumnRouter"/>).
    /// </summary>
    private static PeriodKey OwnerOf(ColumnDef column, PeriodKey viewPeriod, YearCalendar calendar)
    {
        if (column.IsDeleted || column.IsComputed || !calendar.Keys.Contains(viewPeriod.Value))
        {
            return viewPeriod;
        }

        if (column.IsMonthColumn)
        {
            if (column.MonthNumber is null or < 1 or > PeriodCalendar.MonthsInYear)
            {
                return viewPeriod;
            }

            var monthKey = PeriodKey.Create(viewPeriod.Year, column.MonthNumber.Value);
            return calendar.Keys.Contains(monthKey.Value) ? monthKey : viewPeriod;
        }

        return calendar.Anchor ?? viewPeriod;
    }

    private async Task<YearCalendar> CalendarAsync(int projectId, int year, CancellationToken ct)
    {
        if (_calendars.TryGetValue((projectId, year), out var cached))
        {
            return cached;
        }

        var keys = await PeriodCalendar.YearPeriods(_db, projectId, year)
            .Select(p => p.PeriodKeyValue)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var anchor = PeriodCalendar.AnchorOf(keys);
        var calendar = new YearCalendar([.. keys], anchor is { } a ? new PeriodKey(a) : null);
        _calendars[(projectId, year)] = calendar;
        return calendar;
    }

    private sealed record YearCalendar(HashSet<int> Keys, PeriodKey? Anchor);

    private sealed class InstanceRow
    {
        public long Id { get; init; }
        public long DocumentId { get; init; }
        public int ProjectId { get; init; }
        public int TemplateVersionId { get; init; }
        public int TableDefId { get; init; }
        public int PeriodKeyValue { get; init; }
    }
}
