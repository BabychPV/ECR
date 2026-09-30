namespace Ecr.MethodologyImport.Model;

/// <summary>
/// Формула методології (рядок <c>AF_Formulas</c>). Усі рядки вже обрізані (Trim).
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
}

/// <summary>Константа методології (рядок <c>AF_Constants</c>). Одне ім'я може мати багато значень (за Location/датами).</summary>
public sealed record ConstantDef(
    string Methodology,
    string MethodologyVersion,
    string Name,
    string Parameter,
    string Value,
    string Version,
    string Location,
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
    /// <summary>Елементи з атрибутами <c>FInfo_*</c> / <c>CInfo_*</c>, віднесені до формул / констант.</summary>
    public long FormulaElements { get; internal set; }

    public long ConstantElements { get; internal set; }

    /// <summary>Елементи з атрибутами, що не є ні формулою, ні константою (не використані).</summary>
    public long OtherElementsWithAttributes { get; internal set; }

    /// <summary>Скільки полів (імена, коди, версії, тексти) відрізнялися від свого Trim.</summary>
    public long TrimmedFields { get; internal set; }

    /// <summary>Ім'я/версію методології взято із сегмента шляху (немає придатного <c>MInfo_*</c>).</summary>
    public long MethodologyFromPath { get; internal set; }

    /// <summary>Значення взято з <c>ConfigString</c> (<c>%..\Element%</c>), а не з <c>Value</c>.</summary>
    public long ResolvedFromConfigString { get; internal set; }

    /// <summary>Рядок конфігурації, який резолвер не зміг розкрити (лишилось <c>%…%</c>).</summary>
    public long UnresolvedConfigStrings { get; internal set; }

    /// <summary>Дублікати ключа формули (методологія/версія/ім'я/версія формули).</summary>
    public long DuplicateFormulaKeys { get; internal set; }
}
