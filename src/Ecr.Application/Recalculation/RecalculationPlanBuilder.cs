using System.Runtime.CompilerServices;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Складає <see cref="RecalculationPlan"/> зі збереженого графа залежностей.
/// </summary>
/// <remarks>
/// ⛔ Це та ланка, якої не існувало. Граф зберігався (тепер), план читався
/// (`RecalculationService.Plan`), а перекласти перший на другий не було чим —
/// і каскадний перерахунок не запускався ніколи (<c>A7-63</c>).
///
/// ⚠ Переклад НЕ тривіальний, і саме тому його не було: граф описує
/// СТРУКТУРУ (<c>TableDefId</c>, <c>RowKey</c>, <c>ColumnDefId</c>), а план
/// адресує ДАНІ (<c>PeriodKey</c>, <c>TableRowId</c>, <c>ColumnDefId</c>).
/// Міст між ними — відповідність «ключ рядка → ідентифікатор рядка» в
/// конкретному екземплярі таблиці за конкретний період.
/// </remarks>
public static class RecalculationPlanBuilder
{
    /// <summary>Вид залежності «комірка» (<c>cfg.FormulaDependency.DependsOnKind</c> = 0).</summary>
    private const byte CellKind = 0;

    /// <summary>Вид залежності «абсолютний період року» (<c>[Period:#k]</c>, WW-03).</summary>
    private const byte AbsoluteKind = 5;

    /// <summary>Будує план для одного екземпляра таблиці за період.</summary>
    /// <param name="snapshot">Структура версії шаблону.</param>
    /// <param name="dependencies">Розкриті залежності формул версії.</param>
    /// <param name="rowIdsByTable">
    /// Таблиця → (ключ рядка → ідентифікатор рядка) у документі за цей період.
    /// </param>
    /// <param name="periodKey">Період, для якого адресуються комірки.</param>
    /// <returns>План, придатний для <see cref="RecalculationService.Plan"/>.</returns>
    public static RecalculationPlan Build(
        TemplateVersionSnapshot snapshot,
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> rowIdsByTable,
        PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(rowIdsByTable);

        var plan = new RecalculationPlan();

        // L2b: структура версії (таблиці, формули, індекс «хто обчислює цю комірку») будується ОДИН раз на
        // знімок, а не на кожен прогін: на великому шаблоні (~30 000 формул) пошук виробників був O(залежності ×
        // формули таблиці × рядки) і коштував секунди на правку однієї комірки.
        var index = SnapshotIndex.For(snapshot);
        var tables = index.Tables;
        var formulas = index.Formulas;

        foreach (var (_, formula) in formulas.Values)
        {
            plan.Declare(formula.Id, formula.EvaluationOrder, formula.IsCrossSheet, formula.IsSnapshot);
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is not { } formulaId || !formulas.ContainsKey(formulaId))
            {
                continue;
            }

            // ⚠ Інші види залежності — шапка, довідник, інший проєкт — не
            // адресуються коміркою цього зрізу, тож у зворотний індекс не
            // входять. Це не пропуск: правка шапки не є правкою комірки, і
            // перерахунок від неї запускається іншим шляхом.
            //
            // ⚠ WW-03: виняток — `[Period:#k]` (вид 5), коли k збігається з номером ЦЬОГО періоду:
            // формула читає свій власний період, і для плану це звичайне ребро на комірку. Решта
            // абсолютних посилань (k ≠ поточного) — міжперіодні й обслуговуються fan-out-ом
            // (`CrossPeriodReaders`), як і відносні.
            var ownPeriodAbsolute = dependency.DependsOnKind == AbsoluteKind
                                    && dependency.PeriodOffset == periodKey.Sequence;
            if (dependency.DependsOnKind != CellKind && !ownPeriodAbsolute)
            {
                continue;
            }

            // ⛔ Зсув періоду ігнорується НАВМИСНО. Залежність від минулого
            // періоду не робить формулу брудною від правки в поточному, а
            // правка в минулому періоді перераховує його власний зріз —
            // де ця ж залежність має зсув 0.
            if (!ownPeriodAbsolute && dependency.PeriodOffset is { } offset && offset != 0)
            {
                continue;
            }

            // ⛔ Рядки беруться з ТІЄЇ таблиці, на яку вказує залежність, а не
            // з тієї, де сталася правка. Один спільний перелік рядків давав би
            // формулі, що читає сусідню таблицю, чужі ідентифікатори — тобто
            // ребро графа в нікуди, і формула не перераховувалася б ніколи.
            // ⛔ D3: залежність від колонки без конкретного рядка — ще й колонковий індекс, незалежно від
            // того, чи є в таблиці живі рядки: адреса видаленого рядка в `_byCell` відсутня, і без цього
            // ребра видалення рядка не перераховувало б залежних підсумків.
            if (dependency.RowKey is null && dependency.ColumnDefId is { } wholeColumnId)
            {
                plan.DependsOnColumn(formulaId, wholeColumnId);
            }

            if (dependency.TableDefId is { } targetTable
                && rowIdsByTable.TryGetValue(targetTable, out var rowIds))
            {
                AddCellEdge(plan, formulaId, dependency, rowIds, periodKey);
            }

            AddFormulaEdges(plan, formulaId, dependency, index);
        }

        return plan;
    }

    /// <summary>Ребро «формула залежить від комірки».</summary>
    private static void AddCellEdge(
        RecalculationPlan plan,
        int formulaId,
        FormulaDependency dependency,
        IReadOnlyDictionary<string, long> rowIdsByKey,
        PeriodKey periodKey)
    {
        if (dependency.ColumnDefId is not { } columnId)
        {
            return;
        }

        // ⚠ Залежність БЕЗ конкретного рядка — це предикат динамічного
        // діапазону (`RowMode = Dynamic`): наперед відомого списку рядків не
        // існує, і формула вважається залежною від УСІХ рядків своєї
        // таблиці. Пропустити її означало б, що додавання рядка в динамічну
        // таблицю не перераховує підсумок.
        if (dependency.RowKey is not { } rowKey)
        {
            foreach (var rowId in rowIdsByKey.Values)
            {
                plan.DependsOnCell(formulaId, new CellAddress(periodKey, rowId, columnId));
            }

            return;
        }

        if (rowIdsByKey.TryGetValue(rowKey, out var id))
        {
            plan.DependsOnCell(formulaId, new CellAddress(periodKey, id, columnId));
        }

        // Рядка немає в цьому екземплярі — залежність просто не спрацює.
        // Це нормально: шаблон описує рядки, яких у конкретному документі
        // може не бути (`RowMode = Dynamic`).
    }

    /// <summary>Ребра «формула залежить від результату іншої формули».</summary>
    private static void AddFormulaEdges(
        RecalculationPlan plan,
        int formulaId,
        FormulaDependency dependency,
        SnapshotIndex index)
    {
        if (dependency.TableDefId is not { } tableId || !index.ByTable.TryGetValue(tableId, out var producers))
        {
            return;
        }

        // Те саме правило, що й при публікації (`FormulaOutputs.Produces`): без цього ребра баланс
        // порахувався б раніше за суми, з яких він складається. Індекс лише ЗВУЖУЄ перелік кандидатів до тих, що
        // можуть підійти за колонкою й рядком; остаточне рішення завжди за `Produces`.
        foreach (var candidate in producers.Candidates(dependency.RowKey, dependency.ColumnDefId))
        {
            if (candidate.Id != formulaId)
            {
                plan.DependsOnFormula(formulaId, candidate.Id);
            }
        }
    }

    /// <summary>
    /// Незмінна структура знімка версії для побудови плану: таблиці, живі формули й індекс виробників комірок.
    /// </summary>
    /// <remarks>
    /// ⛔ Час життя — час життя самого знімка (<see cref="ConditionalWeakTable{TKey,TValue}"/> за посиланням): знімок
    /// версії незмінний, а нова ревізія/публікація дає НОВИЙ знімок (<c>MetadataCache</c>, ключ <c>v{id}:r{rev}</c>) і
    /// відповідно новий індекс — окремої інвалідації немає і бути не може. Підпис (кількість таблиць, формул і рядків)
    /// страхує від знімка, який хтось добудував після першої побудови (тести збирають <c>TableDef</c> руками).
    /// </remarks>
    internal sealed class SnapshotIndex
    {
        private static readonly ConditionalWeakTable<TemplateVersionSnapshot, SnapshotIndex> Cache = new();

        private SnapshotIndex(
            long signature,
            Dictionary<int, TableDef> tables,
            Dictionary<int, (TableDef Table, FormulaDef Formula)> formulas,
            Dictionary<int, ProducerIndex> byTable)
        {
            Signature = signature;
            Tables = tables;
            Formulas = formulas;
            ByTable = byTable;
        }

        private long Signature { get; }

        /// <summary>Таблиці знімка за ідентифікатором.</summary>
        public Dictionary<int, TableDef> Tables { get; }

        /// <summary>Живі формули знімка за ідентифікатором разом із таблицею.</summary>
        public Dictionary<int, (TableDef Table, FormulaDef Formula)> Formulas { get; }

        /// <summary>Індекс виробників комірок за таблицею.</summary>
        public Dictionary<int, ProducerIndex> ByTable { get; }

        /// <summary>Індекс знімка: з кешу, якщо знімок не змінювався; інакше побудований заново.</summary>
        public static SnapshotIndex For(TemplateVersionSnapshot snapshot)
        {
            var signature = SignatureOf(snapshot);
            if (Cache.TryGetValue(snapshot, out var known) && known.Signature == signature)
            {
                return known;
            }

            var built = Build(snapshot, signature);
            Cache.AddOrUpdate(snapshot, built);

            return built;
        }

        private static long SignatureOf(TemplateVersionSnapshot snapshot)
        {
            long signature = 17;
            foreach (var sheet in snapshot.Sheets)
            {
                foreach (var table in sheet.Tables)
                {
                    signature = (signature * 31) + table.Id;
                    signature = (signature * 31) + table.Formulas.Count;
                    signature = (signature * 31) + table.Rows.Count;
                }
            }

            return signature;
        }

        private static SnapshotIndex Build(TemplateVersionSnapshot snapshot, long signature)
        {
            var tables = snapshot.Sheets
                .SelectMany(s => s.Tables)
                .ToDictionary(t => t.Id);

            var formulas = tables.Values
                .SelectMany(t => t.Formulas.Where(f => !f.IsDeleted).Select(f => (Table: t, Formula: f)))
                .ToDictionary(pair => pair.Formula.Id);

            var byTable = new Dictionary<int, ProducerIndex>(tables.Count);
            foreach (var table in tables.Values)
            {
                byTable[table.Id] = ProducerIndex.Build(table, formulas);
            }

            return new SnapshotIndex(signature, tables, formulas, byTable);
        }
    }

    /// <summary>
    /// Індекс «які формули таблиці можуть обчислювати комірку (рядок × колонка)» — звуження кандидатів для
    /// <see cref="FormulaOutputs.Produces(FormulaDef, string?, string?, int?)"/>.
    /// </summary>
    internal sealed class ProducerIndex
    {
        /// <summary>Ключ кошика формул без власної колонки (рядкові області без явної колонки).</summary>
        private const int NoColumn = int.MinValue;

        private readonly Dictionary<int, Bucket> _byColumn = [];

        private ProducerIndex()
        {
        }

        /// <summary>Будує індекс живих формул таблиці у порядку <see cref="TableDef.Formulas"/>.</summary>
        public static ProducerIndex Build(TableDef table, Dictionary<int, (TableDef Table, FormulaDef Formula)> live)
        {
            var index = new ProducerIndex();
            var rowKeyById = new Dictionary<int, string>();
            foreach (var row in table.Rows)
            {
                // Перший рядок за id — як `FirstOrDefault` у `FormulaOutputs.RowKeyOf`.
                rowKeyById.TryAdd(row.Id, row.RowKeyValue);
            }

            var order = 0;
            foreach (var formula in table.Formulas)
            {
                if (formula.IsDeleted || !live.ContainsKey(formula.Id))
                {
                    continue;
                }

                // Те саме, що `FormulaOutputs.RowKeyOf`, але без лінійного пошуку; колонкова область ключа не читає.
                string? rowKey = null;
                if (formula.Scope != Ecr.Domain.Enums.FormulaScope.Column
                    && formula.RowDefId is { } rowDefId
                    && rowKeyById.TryGetValue(rowDefId, out var found))
                {
                    rowKey = found;
                }

                var key = formula.ColumnDefId ?? NoColumn;
                if (!index._byColumn.TryGetValue(key, out var bucket))
                {
                    index._byColumn[key] = bucket = new Bucket();
                }

                bucket.Add(new Entry(order++, formula, rowKey));
            }

            return index;
        }

        /// <summary>Формули, що обчислюють комірку, у порядку <see cref="TableDef.Formulas"/>.</summary>
        public IEnumerable<FormulaDef> Candidates(string? rowKey, int? columnDefId)
        {
            List<Entry>? found = null;

            // Формула з власною колонкою C підходить лише ціль у колонці C; формула без колонки — будь-якій цілі
            // (рядкова область) або лише цілі без колонки (решта областей) — `Produces` вирішує остаточно.
            if (columnDefId is { } column && _byColumn.TryGetValue(column, out var own))
            {
                own.Collect(rowKey, ref found);
            }

            if (_byColumn.TryGetValue(NoColumn, out var none))
            {
                none.Collect(rowKey, ref found);
            }

            if (found is null)
            {
                yield break;
            }

            if (found.Count > 1)
            {
                found.Sort(static (a, b) => a.Order.CompareTo(b.Order));
            }

            foreach (var entry in found)
            {
                if (FormulaOutputs.Produces(entry.Formula, entry.RowKey, rowKey, columnDefId))
                {
                    yield return entry.Formula;
                }
            }
        }

        private readonly record struct Entry(int Order, FormulaDef Formula, string? RowKey);

        private sealed class Bucket
        {
            private readonly List<Entry> _columnScope = [];
            private readonly List<Entry> _withoutRow = [];
            private readonly Dictionary<string, List<Entry>> _byRow = new(StringComparer.Ordinal);

            public void Add(Entry entry)
            {
                if (entry.Formula.Scope == Ecr.Domain.Enums.FormulaScope.Column)
                {
                    _columnScope.Add(entry);
                }
                else if (entry.RowKey is null)
                {
                    _withoutRow.Add(entry);
                }
                else
                {
                    if (!_byRow.TryGetValue(entry.RowKey, out var list))
                    {
                        _byRow[entry.RowKey] = list = [];
                    }

                    list.Add(entry);
                }
            }

            public void Collect(string? rowKey, ref List<Entry>? into)
            {
                if (_columnScope.Count > 0)
                {
                    (into ??= []).AddRange(_columnScope);
                }

                if (rowKey is null)
                {
                    if (_withoutRow.Count > 0)
                    {
                        (into ??= []).AddRange(_withoutRow);
                    }
                }
                else if (_byRow.TryGetValue(rowKey, out var list))
                {
                    (into ??= []).AddRange(list);
                }
            }
        }
    }
}
