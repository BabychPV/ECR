using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Контрольна точка трекера змін на початок транзакції: перед повтором стратегії виконання
/// повертає трекер у стан, у якому він був ДО невдалої спроби (L6-15 / N1-03).
/// </summary>
/// <remarks>
/// ⛔ Що було. Повтор (1205, обрив з'єднання) виконує замикання вдруге над ТИМ САМИМ трекером, а база
/// відкотила все, що зробила перша спроба. Трекер же пам'ятав її наслідки. Сутність, завантажену
/// ДО транзакції й змінену в замиканні, перше успішне <c>SaveChanges</c> «приймало» (<c>Unchanged</c>);
/// коли відкат наставав пізніше (коміт, наступний оператор), повтор не бачив різниці між трекером і
/// базою — зміна мовчки губилася, а обробник відповідав 200. Видалена сутність після прийняття
/// ставала <c>Detached</c>, змінена — лишалася з уже змінени́ми значеннями, тож доменна перевірка
/// стану («аркуш уже поданий») на другій спробі відмовляла.
///
/// ⚠ Чому не <c>SaveChanges(acceptAllChangesOnSuccess: false)</c> + <c>AcceptAllChanges()</c> після
/// коміту (так було записано в аудиті). Транзакційне замикання часто зберігає НЕ раз: додати →
/// зберегти → використати згенерований ключ → зберегти знову. Без прийняття після кожного збереження
/// друге <c>SaveChanges</c> вставило б перше вдруге. Тому збереження лишаються як були, а стан
/// ВІДНОВЛЮЄТЬСЯ перед повтором.
///
/// ⚠ Що зберігається. Тільки те, що справді потрібне:
/// <list type="bullet">
/// <item>Кожна сутність, що була в трекері на початку, — щоб відрізнити її від створеної спробою
/// (ту відчіплює повтор, як і раніше).</item>
/// <item>Сутності, які на початку вже були <c>Modified</c> чи <c>Deleted</c> (змінені викликачем ДО
/// транзакції): стан, початкові й поточні значення, перелік змінених властивостей.</item>
/// <item>Сутності, чисті на початку, у момент першого збереження, що їх зачіпає
/// (<see cref="DbContext.SavingChanges"/>): початкові значення ДО прийняття. Поточні відновлюються
/// рівними початковим — замикання повторить зміну саме так, як зробило вперше.</item>
/// <item>Сутності, додані (<c>Added</c>) викликачем ДО транзакції (AN-101): поточні значення й те, які
/// з них тимчасові (ключ IDENTITY і FK на ще не вставлений principal). Перша спроба їх вставляла й
/// приймала (<c>Unchanged</c> зі справжнім ключем, або <c>Detached</c>, якщо замикання їх ще й
/// видалило), а після відкату повтор нічого не вставляв: рядок мовчки губився, обробник відповідав
/// 200. Перед повтором кожна знову стає <c>Added</c> зі значеннями на початок і тими самими
/// тимчасовими ключами — зв'язки principal↔dependent зберігаються за значенням ключа, без
/// покладання на fixup навігацій. Ключ, заданий клієнтом (не тимчасовий), лишається як був.</item>
/// </list>
/// </remarks>
internal sealed class ChangeTrackerCheckpoint : IDisposable
{
    private readonly DbContext _db;
    private readonly HashSet<object> _tracked = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Snapshot> _snapshots = new(ReferenceEqualityComparer.Instance);
    private readonly List<AddedSnapshot> _added = [];

    /// <summary>Знімає контрольну точку й починає стежити за збереженнями.</summary>
    /// <param name="db">Контекст.</param>
    public ChangeTrackerCheckpoint(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;

        // ⚠ `Entries()` сам запускає DetectChanges: зміни викликача ДО транзакції видно як Modified.
        foreach (var entry in db.ChangeTracker.Entries())
        {
            // ⚠ Додані — НЕ в `_tracked`: крок 1 відновлення їх відчіплює, крок 4 додає наново.
            if (entry.State == EntityState.Added)
            {
                _added.Add(AddedSnapshot.Of(entry));
                continue;
            }

            _tracked.Add(entry.Entity);

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                _snapshots[entry.Entity] = Snapshot.OfCallerChange(entry);
            }
        }

        db.SavingChanges += OnSavingChanges;
    }

    /// <summary>Перестає стежити за збереженнями.</summary>
    public void Dispose() => _db.SavingChanges -= OnSavingChanges;

    /// <summary>
    /// Повертає трекер у стан на початок транзакції: створене спробою відчіплюється, змінене й
    /// збережене — відновлюється, додане викликачем — знову <c>Added</c>.
    /// </summary>
    public void Restore()
    {
        // 1. Усе, чого не було в трекері на початку (спроба створила чи завантажила наново), і додане
        //    викликачем (крок 4 додасть його наново).
        foreach (var entry in _db.ChangeTracker.Entries()
                     .Where(e => !_tracked.Contains(e.Entity))
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        // 2. Незбережені зміни сутностей, які були в трекері на початку, а до жодного збереження не
        //    дійшли: початкові значення не прийняті, тож повертаємо поточні до них.
        foreach (var entry in _db.ChangeTracker.Entries()
                     .Where(e => _tracked.Contains(e.Entity)
                                 && !_snapshots.ContainsKey(e.Entity)
                                 && e.State is EntityState.Modified or EntityState.Deleted)
                     .ToList())
        {
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
        }

        // 3. Збережені першою спробою (і відкочені з нею) — з контрольної точки.
        foreach (var snapshot in _snapshots.Values)
        {
            snapshot.Apply(_db.Entry(snapshot.Entity));
        }

        // 4. Додані викликачем ДО транзакції (AN-101): крок 1 їх відчепив — що б із ними не зробила
        //    спроба (вставила й прийняла, змінила, видалила), — тепер вони знову Added як на початку.
        foreach (var added in _added)
        {
            added.Apply(_db.Entry(added.Entity));
        }
    }

    private void OnSavingChanges(object? sender, SavingChangesEventArgs e)
    {
        foreach (var entry in _db.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted
                && _tracked.Contains(entry.Entity)
                && !_snapshots.ContainsKey(entry.Entity))
            {
                _snapshots[entry.Entity] = Snapshot.OfClosureChange(entry);
            }
        }
    }

    /// <summary>Сутність, додана викликачем ДО транзакції: значення на початок і тимчасові властивості.</summary>
    private sealed class AddedSnapshot
    {
        private readonly PropertyValues _current;
        private readonly string[] _temporary;

        private AddedSnapshot(object entity, PropertyValues current, string[] temporary)
        {
            Entity = entity;
            _current = current;
            _temporary = temporary;
        }

        public object Entity { get; }

        public static AddedSnapshot Of(EntityEntry entry)
            => new(
                entry.Entity,
                entry.CurrentValues.Clone(),
                [.. entry.Properties.Where(p => p.IsTemporary).Select(p => p.Metadata.Name)]);

        public void Apply(EntityEntry entry)
        {
            // ⚠ Сутність відчеплена (крок 1): у мапі ідентичності немає ні її справжнього ключа з першої
            // спроби, ні тимчасового. Значення — на початок; тимчасові позначаються тимчасовими знову,
            // тож EF не надішле їх у INSERT (IDENTITY), а FK залежної збігається з тимчасовим ключем principal.
            entry.State = EntityState.Added;
            entry.CurrentValues.SetValues(_current);
            foreach (var name in _temporary)
            {
                entry.Property(name).IsTemporary = true;
            }
        }
    }

    private sealed class Snapshot
    {
        private readonly EntityState _state;
        private readonly PropertyValues _original;
        private readonly PropertyValues? _current;
        private readonly HashSet<string> _modified;

        private Snapshot(
            object entity, EntityState state, PropertyValues original, PropertyValues? current, HashSet<string> modified)
        {
            Entity = entity;
            _state = state;
            _original = original;
            _current = current;
            _modified = modified;
        }

        public object Entity { get; }

        /// <summary>Сутність, уже змінена чи видалена викликачем ДО транзакції: відновлюється як була.</summary>
        public static Snapshot OfCallerChange(EntityEntry entry)
            => new(
                entry.Entity,
                entry.State,
                entry.OriginalValues.Clone(),
                entry.CurrentValues.Clone(),
                [.. entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name)]);

        /// <summary>Сутність, чиста на початку: відновлюється чистою, з початковими значеннями до збереження.</summary>
        public static Snapshot OfClosureChange(EntityEntry entry)
            => new(entry.Entity, EntityState.Unchanged, entry.OriginalValues.Clone(), current: null, []);

        public void Apply(EntityEntry entry)
        {
            // ⚠ `Unchanged` приєднує й відчеплену (видалену й прийняту) сутність за її ключем; уже
            // відстежена змінена лише «приймає» значення — їх одразу перезаписує те, що нижче.
            entry.State = EntityState.Unchanged;
            entry.OriginalValues.SetValues(_original);
            entry.CurrentValues.SetValues(_current ?? _original);

            switch (_state)
            {
                case EntityState.Modified:
                    entry.State = EntityState.Modified;
                    foreach (var property in entry.Properties.Where(p => !p.Metadata.IsPrimaryKey()))
                    {
                        property.IsModified = _modified.Contains(property.Metadata.Name);
                    }

                    break;

                case EntityState.Deleted:
                    entry.State = EntityState.Deleted;
                    break;

                default:
                    // `SetValues` міг позначити властивості зміненими; чисте — це і є Unchanged.
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }
}
