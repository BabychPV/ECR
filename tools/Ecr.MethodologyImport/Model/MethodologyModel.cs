namespace Ecr.MethodologyImport.Model;

/// <summary>
/// Формула методології — версійний елемент <c>Methodologies\…\Formulas\&lt;Ім'я&gt;\&lt;Версія&gt;</c>
/// (шаблон Formula_El_V; поля FInfo_* — як у <c>AF_Formulas</c>). Усі рядки вже обрізані (Trim).
/// Дати лишаються рядками AF як є: розбір і часовий пояс — справа наступного кроку (V).
/// </summary>
public sealed record FormulaDef(
    string Methodology,
    string MethodologyVersion,
    string Name,
    string Version,
    string Arguments,
    string Text,
    string StartDate,
    string EndDate,
    string IsAvailable,
    string Report,
    string Path)
{
    /// <summary>Ключ формули для звітів: <c>Методологія/Версія/Ім'я/ВерсіяФормули</c>.</summary>
    public string Key => $"{Methodology}/{MethodologyVersion}/{Name}/{Version}";

    /// <summary>Формула бере участь у розрахунках (<c>FInfo_IsAvailable</c> = True/1).</summary>
    public bool Available => IsAvailable is "True" or "true" or "1";
}

/// <summary>
/// Константа методології: визначення <c>Methodologies\…\Constants\&lt;Ім'я&gt;</c> (<see cref="HasValue"/> = false)
/// або значення <c>…\Constants\&lt;Ім'я&gt;\&lt;Категорія&gt;\&lt;Версія&gt;</c> з <c>CInfo_Value</c>
/// (Категорія — це Location/набір: Common, «A Island - HP Flare», EU_AfterMR_A …).
/// </summary>
public sealed record ConstantDef(
    string Methodology,
    string MethodologyVersion,
    string Name,
    string Category,
    string Version,
    bool HasValue,
    string Value,
    string Parameter,
    string StartDate,
    string EndDate,
    string Unit,
    string Path);

/// <summary>Нейтральна модель, зібрана з AF XML. Порядок списків — детермінований (сортування).</summary>
public sealed class MethodologyModel
{
    public required IReadOnlyList<FormulaDef> Formulas { get; init; }

    public required IReadOnlyList<ConstantDef> Constants { get; init; }

    /// <summary>Лічильники якості розбору (звідки взято ім'я/версію, скільки полів обрізано тощо).</summary>
    public required ModelBuildStats BuildStats { get; init; }
}

public sealed class ModelBuildStats
{
    /// <summary>Версійні елементи формул (Formula_El_V) — саме вони стають <see cref="FormulaDef"/>.</summary>
    public long FormulaVersionElements { get; internal set; }

    /// <summary>Елементи-імена формул (Formula_El) без тексту й аргументів: лише контейнер версій.</summary>
    public long FormulaContainers { get; internal set; }

    public long ConstantDefinitions { get; internal set; }

    public long ConstantValueElements { get; internal set; }

    /// <summary>Елементи методологій, що не є формулами чи константами (Rules, Settings, корінь): папка → кількість.</summary>
    public SortedDictionary<string, long> SkippedMethodologyElements { get; } = new(StringComparer.Ordinal);

    /// <summary>Елементи з атрибутами поза <c>Methodologies\</c> (майданчики, довідники…) — не використані.</summary>
    public long ElementsOutsideMethodologies { get; internal set; }

    /// <summary>Скільки полів (імена, коди, версії, тексти) відрізнялися від свого Trim.</summary>
    public long TrimmedFields { get; internal set; }

    /// <summary>Ім'я/версію методології взято із сегмента шляху (немає придатного <c>MInfo_*</c>).</summary>
    public long MethodologyFromPath { get; internal set; }

    /// <summary><c>MInfo_Name/MInfo_Version</c> розкрилися в значення, що суперечить шляху елемента.</summary>
    public long MethodologyPathMismatch { get; internal set; }

    /// <summary>Значення взято з <c>ConfigString</c> (<c>"%..\Element%";</c>), а не з <c>Value</c>.</summary>
    public long ResolvedFromConfigString { get; internal set; }

    /// <summary>ConfigString ПОТРІБНОГО поля, який не є простим літералом (посилання на інші атрибути) — не розкрито; має бути 0.</summary>
    public long ComputedConfigStrings { get; internal set; }

    /// <summary>Дублікати ключа формули (методологія/версія/ім'я/версія формули).</summary>
    public long DuplicateFormulaKeys { get; internal set; }
}
