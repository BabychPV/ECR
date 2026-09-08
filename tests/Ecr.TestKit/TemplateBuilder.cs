using System.Reflection;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.TestKit;

/// <summary>
/// Складає <see cref="TemplateVersionSnapshot"/> для тестів рушія виразів.
/// </summary>
/// <remarks>
/// Ідентифікатори роздаються послідовно і детерміновано: тест має право
/// покладатися на те, що перша колонка має <c>Id = 1</c>. Без цього кожен
/// тест починався б із сорока рядків складання структури, і саме твердження
/// губилося б у підготовці.
/// </remarks>
public sealed class TemplateBuilder
{
    private static readonly PropertyInfo IdProperty =
        typeof(Entity<int>).GetProperty("Id")!;

    private readonly List<SheetDef> _sheets = [];
    private readonly Dictionary<int, ColumnDef> _columns = [];
    private readonly Dictionary<(int TableDefId, string RowKey), RowDef> _rows = [];

    private int _nextId = 1;

    /// <summary>Версія шаблону, яку описує знімок.</summary>
    public int TemplateVersionId { get; init; } = 1;

    /// <summary>Додає аркуш.</summary>
    public SheetDef Sheet(string code)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create(code), Text(code), _sheets.Count + 1);
        SetId(sheet, _nextId++);
        _sheets.Add(sheet);
        return sheet;
    }

    /// <summary>Додає таблицю до аркуша.</summary>
    public TableDef Table(
        SheetDef sheet,
        string code,
        TableRowMode rowMode = TableRowMode.Fixed,
        TableLayoutKind layout = TableLayoutKind.MonthsInColumns)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var table = new TableDef(sheet.Id, EcrCode.Create(code), Text(code), sheet.Tables.Count + 1, layout, rowMode);
        SetId(table, _nextId++);
        sheet.AddTable(table);
        return table;
    }

    /// <summary>Додає колонку.</summary>
    public ColumnDef Column(
        TableDef table,
        string code,
        CellDataType type = CellDataType.Decimal,
        bool isMonthColumn = false,
        int? unitId = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        var column = new ColumnDef(table.Id, EcrCode.Create(code), Text(code), table.Columns.Count + 1, type);
        SetId(column, _nextId++);
        if (isMonthColumn)
        {
            SetProperty(column, nameof(ColumnDef.IsMonthColumn), true);
            SetProperty(column, nameof(ColumnDef.MonthNumber), (byte?)(table.Columns.Count + 1));
        }

        if (unitId is { } id)
        {
            column.SetUnit(id);
        }

        table.AddColumn(column);
        _columns[column.Id] = column;
        return column;
    }

    /// <summary>Додає рядок.</summary>
    public RowDef Row(TableDef table, string rowKey, int? ordinal = null, RowKind kind = RowKind.Item)
    {
        ArgumentNullException.ThrowIfNull(table);

        var row = new RowDef(table.Id, RowKey.Create(rowKey), ordinal ?? table.Rows.Count + 1, Text(rowKey), kind);
        SetId(row, _nextId++);
        table.AddRow(row);
        _rows[(table.Id, rowKey)] = row;
        return row;
    }

    /// <summary>Додає формулу до таблиці.</summary>
    /// <param name="table">Таблиця, якій належить формула.</param>
    /// <param name="expression">Текст виразу — <b>сирий</b>, як його зберігає API.</param>
    /// <param name="scope">Область дії; за замовчуванням — колонка.</param>
    /// <param name="column">Колонка, на яку пишеться формула (для <c>Column</c>).</param>
    /// <param name="row">Рядок, на який пишеться формула (для <c>Row</c>).</param>
    /// <param name="dialect">Діалект; за замовчуванням — шаблонний.</param>
    /// <remarks>
    /// ⛔ Прив'язка йде через <see cref="FormulaDef.AssignColumn"/> і
    /// <see cref="FormulaDef.AssignRow"/> — тобто через ту саму точку входу,
    /// якою користується API (<c>W5.3</c>). Тести виставляли
    /// <c>ColumnDefId</c> рефлексією, поки публічного сеттера не існувало; це
    /// давало формулу у формі, якої в бою не буває, і перевірка тримала
    /// власну правду про те, як виглядає прив'язана формула.
    ///
    /// ⚠ Ідентифікатор роздається з того самого лічильника, що й решті
    /// сутностей: <c>FormulaDefId</c> — ключ ребра в графі залежностей, і без
    /// нього перевірити порядок обчислення не було б чим.
    /// </remarks>
    public FormulaDef Formula(
        TableDef table,
        string expression,
        FormulaScope scope = FormulaScope.Column,
        ColumnDef? column = null,
        RowDef? row = null,
        ExpressionDialect dialect = ExpressionDialect.Template)
    {
        ArgumentNullException.ThrowIfNull(table);

        var formula = new FormulaDef(table.Id, scope, expression, dialect);
        SetId(formula, _nextId++);

        if (column is not null)
        {
            formula.AssignColumn(column.Id);
        }

        if (row is not null)
        {
            formula.AssignRow(row.Id);
        }

        table.AddFormula(formula);
        return formula;
    }

    /// <summary>
    /// Складає <see cref="TemplateVersion"/> з уже доданих аркушів.
    /// </summary>
    /// <param name="templateId">Шаблон, якому належить версія.</param>
    /// <param name="version">Номер версії.</param>
    /// <param name="createdByUserId">Автор.</param>
    /// <param name="utcNow">Момент створення в UTC.</param>
    /// <remarks>
    /// ⛔ Аркуші додаються публічним <see cref="TemplateVersion.AddSheet"/>, а
    /// не підстановкою приватного поля <c>_sheets</c> рефлексією. Різниця не
    /// косметична: підстановка обходила перевірку унікальності коду аркуша і
    /// давала граф, якого в бою не існує — саме те, у чому директива №09 §8.2
    /// звинувачує <c>PublishTemplateVersionTests</c>.
    ///
    /// ⚠ Версія тут — ЧЕРНЕТКА. Публікацію робить сам тест: інакше будівник
    /// вирішував би за нього, у якому стані перевіряти правило.
    /// </remarks>
    public TemplateVersion Version(
        int templateId = 1,
        string version = "1.0.0.0",
        int createdByUserId = 7,
        DateTime? utcNow = null)
    {
        var result = new TemplateVersion(
            templateId, version, createdByUserId,
            utcNow ?? new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc));

        SetId(result, TemplateVersionId);

        foreach (var sheet in _sheets)
        {
            result.AddSheet(sheet);
        }

        return result;
    }

    /// <summary>Позначає рядок видаленим (soft delete, ФВ-7.6).</summary>
    public static void Delete(RowDef row)
    {
        ArgumentNullException.ThrowIfNull(row);
        SetProperty(row, nameof(RowDef.IsDeleted), true);
    }

    /// <summary>Збирає знімок.</summary>
    public TemplateVersionSnapshot Build(int presentationRevision = 0)
        => new(TemplateVersionId, presentationRevision, _sheets, _columns, _rows);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId(Entity<int> entity, int id) => IdProperty.SetValue(entity, id);

    private static void SetProperty(object target, string name, object? value)
        => target.GetType().GetProperty(name)!.SetValue(target, value);
}
