using System.Text;
using System.Xml;

namespace Ecr.MethodologyImport.Reading;

/// <summary>
/// Елемент AF із його прямими атрибутами. <see cref="Path"/> — імена елементів від кореня до цього
/// (останній — власне ім'я). <see cref="Values"/> — статичні значення атрибутів (<c>Value</c>),
/// <see cref="ConfigStrings"/> — рядки конфігурації (<c>ConfigString</c>, напр.
/// <c>%..\..\..\Element%</c>), якщо статичного значення немає.
/// </summary>
public sealed record AfElementRecord(
    IReadOnlyList<string> Path,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> ConfigStrings);

/// <summary>Лічильники читача — «що прочитано» й «що не впізнано» (толерантний розбір).</summary>
public sealed class AfReadStats
{
    public long Elements { get; internal set; }

    public long Attributes { get; internal set; }

    public long ElementsWithoutName { get; internal set; }

    public long DuplicateAttributes { get; internal set; }

    /// <summary>Невідомі теги прямих дітей елемента: тег → кількість. Не падіння, а рядок звіту.</summary>
    public SortedDictionary<string, long> Unrecognized { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Потоковий читач AF XML (експорт PI System Explorer): <c>XmlReader</c>, без DOM — файл ~200 МБ.
/// ⚠ Формат виведено з документів і структури AF, а НЕ зі справжнього файла: очікується вкладена
/// структура <c>AFElement</c> → (<c>Name</c>, <c>AFAttribute</c>[<c>Name</c>, <c>Value</c>|<c>ConfigString</c>],
/// вкладені <c>AFElement</c>). Усе, що читач не знає, лічиться в <see cref="AfReadStats.Unrecognized"/>.
/// Уся прив'язка до формату — у цьому файлі; модель і аналіз про XML нічого не знають.
/// </summary>
public static class AfXmlReader
{
    private static readonly HashSet<string> ElementTags = new(StringComparer.Ordinal) { "AFElement", "Element" };
    private static readonly HashSet<string> AttributeTags = new(StringComparer.Ordinal) { "AFAttribute", "Attribute" };

    // Діти елемента, які читач свідомо пропускає й не вважає «непізнаним».
    private static readonly HashSet<string> KnownIgnored = new(StringComparer.Ordinal)
    {
        "ID", "Id", "Description", "Template", "CategoryRefs", "Categories", "Security",
        "ExtendedProperties", "Type", "Modified", "Created",
    };

    /// <summary>Читає потік і викликає <paramref name="onElement"/> для кожного елемента з ≥ 1 атрибутом.</summary>
    public static AfReadStats Read(Stream input, Action<AfElementRecord> onElement)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(onElement);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };

        var stats = new AfReadStats();
        using var reader = XmlReader.Create(input, settings);
        var noPath = Array.Empty<string>();
        reader.Read();
        while (!reader.EOF)
        {
            // ReadElement залишає читач ПІСЛЯ елемента (як Skip), тому Read() тут лише для решти вузлів.
            if (reader.NodeType == XmlNodeType.Element && ElementTags.Contains(reader.LocalName))
            {
                ReadElement(reader, noPath, onElement, stats);
            }
            else
            {
                reader.Read();
            }
        }

        return stats;
    }

    private static void ReadElement(
        XmlReader r, IReadOnlyList<string> parentPath, Action<AfElementRecord> sink, AfReadStats stats)
    {
        stats.Elements++;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var configs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        string? explicitPath = null;

        if (r.IsEmptyElement)
        {
            r.Read();
        }
        else
        {
            var depth = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType != XmlNodeType.Element)
                {
                    r.Read();
                    continue;
                }

                var tag = r.LocalName;
                if (ElementTags.Contains(tag))
                {
                    ReadElement(r, OwnPath(parentPath, name, explicitPath, stats, countMissing: false), sink, stats);
                }
                else if (AttributeTags.Contains(tag))
                {
                    ReadAttribute(r, values, configs, stats);
                }
                else if (tag == "Name")
                {
                    name = ReadText(r);
                }
                else if (tag == "Path")
                {
                    explicitPath = ReadText(r);
                }
                else
                {
                    if (!KnownIgnored.Contains(tag))
                    {
                        stats.Unrecognized[tag] = stats.Unrecognized.GetValueOrDefault(tag) + 1;
                    }

                    r.Skip();
                }
            }

            r.Read(); // за EndElement
        }

        if (values.Count > 0 || configs.Count > 0)
        {
            sink(new AfElementRecord(OwnPath(parentPath, name, explicitPath, stats, countMissing: true), values, configs));
        }
    }

    private static string[] OwnPath(
        IReadOnlyList<string> parentPath, string? name, string? explicitPath, AfReadStats stats, bool countMissing)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (string.IsNullOrWhiteSpace(name) && countMissing)
        {
            stats.ElementsWithoutName++;
        }

        var path = new string[parentPath.Count + 1];
        for (var i = 0; i < parentPath.Count; i++)
        {
            path[i] = parentPath[i];
        }

        path[^1] = name?.Trim() ?? string.Empty;
        return path;
    }

    private static void ReadAttribute(
        XmlReader r, Dictionary<string, string> values, Dictionary<string, string> configs, AfReadStats stats)
    {
        string? attrName = null;
        string? value = null;
        string? config = null;

        using (var sub = r.ReadSubtree())
        {
            sub.Read(); // на самому елементі атрибута
            if (!sub.IsEmptyElement)
            {
                var depth = sub.Depth;
                sub.Read();
                while (!sub.EOF && !(sub.NodeType == XmlNodeType.EndElement && sub.Depth == depth))
                {
                    if (sub.NodeType == XmlNodeType.Element && sub.Depth == depth + 1)
                    {
                        switch (sub.LocalName)
                        {
                            case "Name":
                                attrName = ReadText(sub);
                                break;
                            case "Value":
                                value = ReadText(sub);
                                break;
                            case "ConfigString":
                                config = ReadText(sub);
                                break;
                            default:
                                sub.Skip();
                                break;
                        }
                    }
                    else
                    {
                        sub.Read();
                    }
                }
            }
        }

        r.Read(); // за елементом атрибута

        stats.Attributes++;
        var key = attrName?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (values.ContainsKey(key) || configs.ContainsKey(key))
        {
            stats.DuplicateAttributes++;
        }

        if (value is not null)
        {
            values[key] = value;
        }

        if (config is not null)
        {
            configs[key] = config;
        }
    }

    /// <summary>Текст елемента (Text/CDATA, вкладені елементи пропускаються); читач після виклику — за елементом.</summary>
    private static string ReadText(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return string.Empty;
        }

        var depth = r.Depth;
        var sb = new StringBuilder();
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    sb.Append(r.Value);
                    r.Read();
                    break;
                case XmlNodeType.Element:
                    r.Skip();
                    break;
                default:
                    r.Read();
                    break;
            }
        }

        r.Read();
        return sb.ToString();
    }
}
