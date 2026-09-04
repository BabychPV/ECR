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
