using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IUnitStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class UnitStore(EcrDbContext db) : IUnitStore
{
    /// <inheritdoc />
    public Task<Unit?> FindUnitByCodeAsync(string code, CancellationToken ct)
        => db.Units.FirstOrDefaultAsync(u => u.Code == code, ct);

    /// <inheritdoc />
    public Task<bool> DimensionExistsAsync(byte dimensionId, CancellationToken ct)
        => db.Dimensions.AnyAsync(d => d.Id == dimensionId, ct);

    /// <inheritdoc />
    public void AddUnit(Unit unit) => db.Units.Add(unit);

    /// <inheritdoc />
    public Task<Unit?> FindUnitByIdAsync(int unitId, CancellationToken ct)
        => db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

    /// <inheritdoc />
    public void RemoveUnit(Unit unit) => db.Units.Remove(unit);

    /// <inheritdoc />
    /// <remarks>
    /// <c>UPDLOCK, HOLDLOCK</c> — той самий прийом, що в <c>MethodologyVersionDeletionStore</c>:
    /// дві правки однієї одиниці серіалізуються на її рядку, а читання під RCSI без підказки
    /// не блокувало б нічого.
    ///
    /// ⚠ Екземпляр, уже відстежуваний контекстом (повтор замикання стратегією після
    /// дедлоку), відчіплюється: інакше EF повернув би його з ПОПЕРЕДНЬОЇ спроби — зі
    /// зміненими в пам'яті значеннями, а не з тими, що зараз у базі.
    /// </remarks>
    public async Task<Unit?> LockUnitAsync(int unitId, CancellationToken ct)
    {
        RequireTransaction();

        var stale = db.ChangeTracker.Entries<Unit>().FirstOrDefault(e => e.Entity.Id == unitId);
        if (stale is not null)
        {
            stale.State = EntityState.Detached;
        }

        return await db.Units
            .FromSql($"SELECT * FROM uom.Unit WITH (UPDLOCK, HOLDLOCK) WHERE Id = {unitId}")
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Чому <c>SERIALIZABLE</c>, а не підказки в кожному запиті. Під RCSI перевірка
    /// читає знімок: незакомічене посилання для неї не існує, а нове після неї нічим не
    /// зупинене. <c>SERIALIZABLE</c> робить читання блокувальним (чекає на незакомічене) і
    /// тримає діапазони ключів до кінця транзакції (нове посилання чекає коміту). Підказки
    /// довелося б дублювати в кожному запиті переліку — і кожне нове джерело посилань
    /// мовчки лишалося б без них.
    ///
    /// ⚠ Рівень ставиться окремим пакетом без параметрів (не <c>sp_executesql</c>, де він
    /// скинувся б на виході) і повертається до <c>READ COMMITTED</c> одразу після
    /// переліку. Блокування, узяті під <c>SERIALIZABLE</c>, лишаються до кінця транзакції й
    /// після повернення рівня — так визначено для <c>SET TRANSACTION ISOLATION LEVEL</c>.
    ///
    /// ⚠ Ціна: запит до таблиці даних без індексу за одиницею тримає діапазон на всю
    /// таблицю до коміту. Це адміністративна дія над довідником, і транзакція коротка.
    /// </remarks>
    public async Task<UsageResponse> FindUnitUsageForUpdateAsync(int unitId, int take, CancellationToken ct)
    {
        RequireTransaction();

        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;", ct).ConfigureAwait(false);
        var usage = await FindUnitUsageAsync(unitId, take, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", ct).ConfigureAwait(false);

        return usage;
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Блокування одиниці береться лише всередині транзакції: поза нею воно звільнилося б " +
                "одразу і нічого не захистило б.");
        }
    }

    /// <inheritdoc />
    public async Task<UsageResponse> FindUnitUsageAsync(int unitId, int take, CancellationToken ct)
    {
        var total = 0;
        var items = new List<UsageItemDto>();

        // Рахує джерело повністю, а в перелік бере лише те, що ще вміщається.
        // ⚠ Джерело приходить уже ВПОРЯДКОВАНИМ: `OrderBy` після проєкції в
        // конструктор запису EF не перекладає.
        async Task AddAsync(string kind, IQueryable<Hit> source)
        {
            total += await source.CountAsync(ct).ConfigureAwait(false);
            if (items.Count >= take)
            {
                return;
            }

            var page = await source.Take(take - items.Count).ToListAsync(ct).ConfigureAwait(false);
            items.AddRange(page.Select(h => new UsageItemDto(
                kind, h.Id.ToString(CultureInfo.InvariantCulture), h.Label, h.Route)));
        }

        await AddAsync(
            UsageKinds.TemplateColumn,
            from c in db.ColumnDefs
            where c.UnitId == unitId
            join t in db.TableDefs on c.TableDefId equals t.Id
            join s in db.SheetDefs on t.SheetDefId equals s.Id
            join v in db.TemplateVersions on s.TemplateVersionId equals v.Id
            orderby c.Id
            select new Hit(
                c.Id, t.Code + "." + c.Code, "/admin/templates/" + v.TemplateId + "/versions/" + v.Id))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.RegistryField,
            from f in db.RegistryFieldDefs
            where f.UnitId == unitId
            join r in db.RegistryDefs on f.RegistryDefId equals r.Id
            orderby f.Id
            select new Hit(f.Id, r.Code + "." + f.Code, "/admin/registries/" + r.Code + "/definition"))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.MethodologyConstant,
            from c in db.MethodologyConstants
            where c.UnitId == unitId
            join v in db.MethodologyVersions on c.MethodologyVersionId equals v.Id
            orderby c.Id
            select new Hit(c.Id, c.Code, "/admin/methodologies/" + v.MethodologyId + "/versions"))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.MethodologyFormula,
            from f in db.MethodologyFormulas
            where f.OutputUnitId == unitId
            join v in db.MethodologyVersions on f.MethodologyVersionId equals v.Id
            orderby f.Id
            select new Hit(f.Id, f.Code, "/admin/methodologies/" + v.MethodologyId + "/versions"))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.MethodologyOutput,
            from o in db.MethodologyOutputs
            where o.UnitId == unitId
            join v in db.MethodologyVersions on o.MethodologyVersionId equals v.Id
            orderby o.Id
            select new Hit(o.Id, o.Code, "/admin/methodologies/" + v.MethodologyId + "/versions"))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.FieldMap,
            db.EntityFieldMaps
                .Where(m => m.SourceUnitId == unitId || m.TargetUnitId == unitId)
                .OrderBy(m => m.Id)
                .Select(m => new Hit(m.Id, m.SourceField, "/admin/mapping")))
            .ConfigureAwait(false);

        // HSE301 F9: мапінг подій джерела тримає одиницю атрибута і одиницю колонки
        // (FK_SEFM_SourceUnit / FK_SEFM_TargetUnit) — це теж відповідність «поле джерела →
        // колонка», тому вид той самий, що в EntityFieldMap. Сторінки мапінгу подій ще немає,
        // маршруту нема; ідентифікатор — власний у своїй таблиці, а мітка називає мапінг подій.
        await AddAsync(
            UsageKinds.FieldMap,
            db.SourceEventFieldMaps
                .Where(m => m.SourceUnitId == unitId || m.TargetUnitId == unitId)
                .OrderBy(m => m.Id)
                .Select(m => new Hit(m.Id, "event:" + m.SourceAttribute, null)))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.UnitConversion,
            from c in db.UnitConversions
            where c.FromUnitId == unitId || c.ToUnitId == unitId
            join f in db.Units on c.FromUnitId equals f.Id
            join t in db.Units on c.ToUnitId equals t.Id
            orderby c.Id
            select new Hit(c.Id, f.Code + " -> " + t.Code, "/admin/units"))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.DerivedUnit,
            db.Units
                .Where(u => u.NumeratorUnitId == unitId || u.DenominatorUnitId == unitId)
                .OrderBy(u => u.Id)
                .Select(u => new Hit(u.Id, u.Code, "/admin/units")))
            .ConfigureAwait(false);

        // ⚠ Базова одиниця розмірності: через неї йде кожна конверсія, тож
        // вона не видаляється ніколи — і це видно тим самим переліком, без
        // окремого правила в обробнику.
        await AddAsync(
            UsageKinds.DimensionBase,
            db.Dimensions
                .Where(d => d.BaseUnitId == unitId)
                .OrderBy(d => d.Id)
                .Select(d => new Hit(d.Id, d.Code, null)))
            .ConfigureAwait(false);

        // HSE301 U2: прив'язка PI тримає одиницю і цільовою колонкою (FK_RWM_Unit), і
        // кожним джерелом (FK_RWS_Unit). Джерело без своєї прив'язки не існує, тож рядок —
        // один на прив'язку, хоч би скільки її джерел були в цій одиниці. Раніше цього
        // виду не було, і видалення такої одиниці падало на FK голим 500.
        await AddAsync(
            UsageKinds.RowWindowMap,
            from m in db.RowWindowMaps
            where m.TargetUnitId == unitId || m.Sources.Any(s => s.SourceUnitId == unitId)
            join c in db.ColumnDefs on m.TargetColumnDefId equals c.Id
            join t in db.TableDefs on m.TableDefId equals t.Id
            join s in db.SheetDefs on t.SheetDefId equals s.Id
            join v in db.TemplateVersions on s.TemplateVersionId equals v.Id
            orderby m.Id
            select new Hit(
                m.Id, t.Code + "." + c.Code, "/admin/templates/" + v.TemplateId + "/versions/" + v.Id))
            .ConfigureAwait(false);

        // Таблиці даних: один рядок на таблицю, без підрахунку (див. порт).
        // ⚠ Кожен зовнішній ключ на uom.Unit мусить мати тут або вище свій запит — інакше
        // видалення падає на FK голим 500 замість 409. Стереже UnitUsageForeignKeyTests.
        foreach (var (table, any) in new (string, Func<Task<bool>>)[]
        {
            ("doc.CellValue", () => db.CellValues.AnyAsync(x => x.ValueUnitId == unitId, ct)),
            ("doc.DocumentHeaderValue", () => db.DocumentHeaderValues.AnyAsync(x => x.ValueUnitId == unitId, ct)),
            ("dic.RegistryValue", () => db.RegistryValues.AnyAsync(x => x.ValueUnitId == unitId, ct)),
            ("calc.CalculationResult", () => db.CalculationResults.AnyAsync(x => x.UnitId == unitId, ct)),
            ("ext.RawData", () => RawPointsUseUnitAsync(unitId, ct)),
            ("ext.RowWindowValue", () => db.RowWindowValues.AnyAsync(x => x.TargetUnitId == unitId, ct)),
        })
        {
            if (!await any().ConfigureAwait(false))
            {
                continue;
            }

            total++;
            if (items.Count < take)
            {
                items.Add(new UsageItemDto(UsageKinds.Data, table, table, null));
            }
        }

        return new UsageResponse(total, items);
    }

    /// <summary>
    /// Чи тримає одиницю хоч одна сира точка — БЕЗ сканування <c>ext.RawDataPoint</c> (R2a).
    /// </summary>
    /// <remarks>
    /// ⛔ Було <c>RawDataPoints.AnyAsync(UnitId == …)</c>: індексу за <c>UnitId</c> немає, тож для
    /// невикористаної одиниці (саме її й видаляють) це ПОВНИЙ скан таблиці, що росте на ~263 млн
    /// рядків на рік, а під <c>SERIALIZABLE</c> (<see cref="FindUnitUsageForUpdateAsync"/>) ще й
    /// діапазонне блокування всієї таблиці — збирач чекав би на вставках.
    /// <para>
    /// Тепер: від мапінгів, що називають одиницю джерелом (<c>SourceUnitId</c>), — seek у
    /// <c>UQ_RawDataPoint</c> за <c>(SourceEntityId, SourcePath = SourceField)</c>. Вартість
    /// залежить від точок ЦИХ шляхів, а не від чужих рядків. Точки лягають лише по змаплених
    /// шляхах (<c>CollectionRunner</c> зіставляє <c>SourceField == SourcePath</c>), а їхня одиниця —
    /// символ джерела, що збігається з <c>SourceUnitId</c> мапінгу. <c>PendingSourceUnitId</c> сюди
    /// не входить: на ньому немає FK, а храповик <c>UnitUsageForeignKeyTests</c> не приймає читання
    /// стовпців без ключа.
    /// </para>
    /// <para>
    /// ⚠ Свідомий компроміс без індексу (індекс <c>UnitId</c> відкладено до реструктуризації R2b):
    /// «осиротілі» точки — мапінг згодом видалено або йому змінили <c>SourceUnitId</c> — цим
    /// запитом не видно; такі точки лишаються під захистом <c>FK_RDP_Unit</c> (547), а не 409 з
    /// переліком. Точна перевірка повернеться разом з індексом чи кластером R2b.
    /// </para>
    /// </remarks>
    private async Task<bool> RawPointsUseUnitAsync(int unitId, CancellationToken ct)
    {
        // ⚠ Пари (сутність, шлях) — ОКРЕМИМ запитом, далі по запиту на пару з параметрами:
        // корельований EXISTS / CROSS APPLY оптимізатор компілює в скан кластерного індексу
        // (виміряно: читання ростуть з чужими рядками), а рівність за двома провідними стовпцями
        // UQ_RawDataPoint з параметрами дає seek. Порожній набір пар таблицю не читає.
        var pairs = await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceUnitId == unitId)
            .Select(m => new { m.SourceEntityId, m.SourceField })
            .Distinct()
            .OrderBy(x => x.SourceEntityId)
            .ThenBy(x => x.SourceField)
            .Take(MaxRawPairs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var pair in pairs)
        {
            var entityId = pair.SourceEntityId;
            var path = pair.SourceField;

            if (await db.RawDataPoints.AnyAsync(
                    p => p.SourceEntityId == entityId && p.SourcePath == path && p.UnitId == unitId, ct)
                .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Стеля пар «сутність × шлях» мапінгів, за якими шукаються сирі точки одиниці.</summary>
    private const int MaxRawPairs = 1_000;

    /// <summary>Проміжний рядок пошуку посилань.</summary>
    private sealed record Hit(int Id, string Label, string? Route);
}
