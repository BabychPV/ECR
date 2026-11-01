using Ecr.Application.RegistryImport;

namespace Ecr.Application.Tests.RegistryImport;

/// <summary>
/// An in-memory sheet laid out like the ERA table 3.3 form: title rows, a three-row merged header, the column-number row,
/// group rows, sources merged vertically over A-T, substances in W-AB, a totals block at the bottom.
/// </summary>
internal sealed class EraGrid
{
    public const int NaeColumn = 7;
    public const int SubstanceCodeColumn = 23;

    private int _row = 8;

    public InMemoryImportGrid Grid { get; } = new();

    public EraGrid()
    {
        var g = Grid;
        g.Set(1, 1, "ERA test form").Merge(1, 1, 1, 10).Set(1, 27, "Table 3.3").Merge(1, 27, 1, 28);
        g.Set(2, 1, "Parameters of emissions").Merge(2, 1, 2, 10);
        g.Set(3, 1, "Test sea (test field), NDV-2024").Merge(3, 1, 3, 13);

        Head(4, 1, "Произ-водство", 6);
        Head(4, 2, "Цех", 6);
        g.Set(4, 3, "Источник выделения загрязняющих веществ").Merge(4, 3, 5, 4);
        g.Set(6, 3, "Наименование").Set(6, 4, "Количество, шт.");
        Head(4, 5, "Число часов  работы в году", 6);
        Head(4, 6, "Наименование  источника выброса вредных веществ", 6);
        Head(4, 7, "Номер источника выбросов на карте-схеме", 6);
        Head(4, 8, "Т И П    И З А", 6);
        Head(4, 9, "Высота источника выбросов, м", 6);
        Head(4, 10, "Диаметр устья трубы, м", 6);
        g.Set(4, 11, "Параметры газовоздушной смеси на выходе из трубы").Merge(4, 11, 5, 13);
        g.Set(6, 11, "Скорость, м/с").Set(6, 12, "Объем смеси,                м3/с").Set(6, 13, "Темпе-ратура смеси, оC");
        g.Set(4, 14, "Координаты источника на карте-схеме,м.").Merge(4, 14, 4, 17);
        g.Set(5, 14, "точ.ист, /1-го конца").Merge(5, 14, 5, 15);
        g.Set(5, 16, "2-го конца / длина, ширина").Merge(5, 16, 5, 17);
        g.Set(6, 14, "Х1").Set(6, 15, "Y1").Set(6, 16, "Х2").Set(6, 17, "Y2");
        Head(4, 18, "Угол поворота площадного источника, град.", 6);
        Head(4, 19, "Hаименование газоочистных установок", 6);
        Head(4, 20, "Вещество, по которому производится газоочистка", 6);
        Head(4, 21, "Коэффи-циент обеспеченности газоочисткой, %", 6);
        Head(4, 22, "Среднеэксплуа-тационная степень очистки", 6);
        Head(4, 23, "Код вещества", 6);
        Head(4, 24, "Hаименование вещества", 6);
        g.Set(4, 25, "Выбросы загрязняющего вещества").Merge(4, 25, 5, 27);
        g.Set(6, 25, "г/с").Set(6, 26, "мг/нм3").Set(6, 27, "т/год");
        Head(4, 28, "Год дости-жения НДВ", 6);

        var numbers = new[] { "1", "2", "3", "4", "5", "6", "7", "7а", "8", "9", "10", "11", "12", "13", "14", "15", "16", "16a", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26" };
        for (var c = 0; c < numbers.Length; c++)
        {
            g.Set(7, c + 1, numbers[c]);
        }
    }

    public int NextRow => _row + 1;

    private void Head(int row, int column, string text, int lastRow)
    {
        Grid.Set(row, column, text);
        Grid.Merge(row, column, lastRow, column);
    }

    /// <summary>A group row merged across A:AB.</summary>
    public EraGrid Group(string label)
    {
        _row++;
        Grid.Set(_row, 1, label).Merge(_row, 1, _row, 28);
        return this;
    }

    /// <summary>A source: A-T merged over the substance rows, substances below it.</summary>
    public EraGrid Source(
        object nae, string name, string type, IReadOnlyList<(object Code, string Name, object? Gs, object? Mg, object? Tons)> substances,
        object? height = null, object? diameter = null, object? production = null, object? quantity = null)
    {
        var first = _row + 1;
        var last = first + Math.Max(substances.Count, 1) - 1;

        Grid.Set(first, 1, production ?? 101d);
        Grid.Set(first, 3, name);
        Grid.Set(first, 4, quantity ?? "131");
        Grid.Set(first, 5, 8784d);
        Grid.Set(first, 6, "Test point");
        Grid.Set(first, NaeColumn, nae);
        Grid.Set(first, 8, type);
        Grid.Set(first, 9, height ?? 9.5d);

        if (diameter is not null)
        {
            Grid.Set(first, 10, diameter);
        }

        Grid.Set(first, 13, 34.1d).Set(first, 14, 9597489d).Set(first, 15, 5145573d);

        for (var c = 1; c <= 20; c++)
        {
            if (last > first)
            {
                Grid.Merge(first, c, last, c);
            }
        }

        for (var i = 0; i < substances.Count; i++)
        {
            var s = substances[i];
            var r = first + i;
            Grid.Set(r, SubstanceCodeColumn, s.Code).Set(r, 24, s.Name);
            Grid.Set(r, 25, s.Gs).Set(r, 26, s.Mg).Set(r, 27, s.Tons).Set(r, 28, 2024d);
        }

        _row = last;
        return this;
    }

    /// <summary>Blank rows and a totals block (formulas) after the data.</summary>
    public EraGrid Totals()
    {
        _row += 4;
        Grid.Set(_row, 24, "TOTAL").Set(_row, 27, 1d, hasFormula: true);

        for (var i = 1; i <= 2; i++)
        {
            Grid.Set(_row + i, 24, "Block " + i).Set(_row + i, 27, 2d, hasFormula: true);
        }

        return this;
    }

    public static (object, string, object?, object?, object?) Sub(object code, string name)
        => (code, name, 0.1d, null, 1d);

    public static (object, string, object?, object?, object?) Sub(object code, string name, object? gs, object? mg = null, object? tons = null)
        => (code, name, gs, mg, tons);
}
