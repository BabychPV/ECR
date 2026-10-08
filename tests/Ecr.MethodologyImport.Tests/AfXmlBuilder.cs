using System.Security;
using System.Text;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// Синтетичний AF XML за РЕАЛЬНОЮ структурою <c>ECR_01_Air.xml</c> (AF 3.1, «Flat»; це не копія файла):
/// корінь <c>AF</c> → <c>AFDatabase</c> → плаский перелік <c>AFElement</c> з повним шляхом у <c>Name</c>.
/// Формула — <c>Methodologies\М\ВерсіяМ\Formulas\Ф\ВерсіяФ</c> (Formula_El_V, FInfo_*, MInfo_* — ConfigString
/// <c>"%..\..\..\..\Element%";</c>); константа — <c>…\Constants\К</c> і значення <c>…\Constants\К\Категорія\V1</c>.
/// Методологія може бути вкладеною (шлях <c>Група\Підгрупа\ECW_01</c>).
/// </summary>
internal sealed class AfXmlBuilder
{
    private readonly List<string> _elements = [];
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);

    public static string Value(string name, string value)
        => $"<AFAttribute><id>x</id><Name>{Esc(name)}</Name><Type>String</Type><Value type=\"String\">{Esc(value)}</Value><DataReference /></AFAttribute>";

    /// <summary>Атрибут String Builder: <c>"%..\Element%";</c> (у файлі ще й <c>&amp;#xD;</c> — перенос рядка).</summary>
    public static string Config(string name, string literal)
        => $"<AFAttribute><id>x</id><Name>{Esc(name)}</Name><Type>String</Type><DataReference>String Builder</DataReference><ConfigString>\"{Esc(literal)}\";&#xD;\n</ConfigString></AFAttribute>";

    /// <summary>Обчислюваний String Builder (посилання на інші атрибути): читач його не використовує.</summary>
    public static string Computed(string name)
        => $"<AFAttribute><id>x</id><Name>{Esc(name)}</Name><Type>String</Type><DataReference>String Builder</DataReference><ConfigString>'MInfo_Name';_;'FInfo_Name';&#xD;\n</ConfigString></AFAttribute>";

    public static string Element(string path, string template, string attributes)
        => $"<AFElement ReferenceType=\"Parent-Child\"><id>x</id><Name>{Esc(path)}</Name><Description />"
           + $"<Template>{Esc(template)}</Template><IsAnnotated>false</IsAnnotated><Modifier>u</Modifier><Comment />"
           + $"{attributes}<SecurityAccessControl>Administrators:A(r)</SecurityAccessControl></AFElement>";

    private static string Esc(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static string Ups(int count) => string.Concat(Enumerable.Repeat("..\\", count));

    /// <summary>Формула <paramref name="m"/>/<paramref name="mv"/>; поля передаються «як у AF» (з пробілами).</summary>
    public AfXmlBuilder Formula(
        string m, string mv, string name, string args, string text = "1", string version = "V1",
        string available = "True", string? extraAttributes = null)
    {
        var folder = FolderPath(m, mv, "Formulas");
        EmitMethodology(m, mv);
        var container = $"{folder}\\{name}";
        if (_emitted.Add(container))
        {
            _elements.Add(Element(container, "Formula_El", Config("FInfo_Name", "%Element%")));
        }

        var segs = container.Split('\\').Length + 1;          // довжина шляху версійного елемента
        var nameIdx = 1 + m.Split('\\').Length - 1;           // індекс імені методології в шляху
        var attrs = new StringBuilder()
            .Append(Config("FInfo_Name", "%..\\Element%"))
            .Append(Config("FInfo_Version", "%Element%"))
            .Append(Value("FInfo_Arguments", args))
            .Append(Value("FInfo_Text", text))
            .Append(Value("FInfo_StartDate", "2023-12-31T19:00:00Z"))
            .Append(Value("FInfo_EndDate", "9999-02-19T19:00:00Z"))
            .Append(Value("FInfo_IsAvailable", available))
            .Append(Config("MInfo_Name", "%" + Ups(segs - 1 - nameIdx) + "Element%"))
            .Append(Config("MInfo_Version", "%" + Ups(segs - 2 - nameIdx) + "Element%"))
            .Append(Computed("FInfo_UniqueName"))
            .Append(extraAttributes);
        _elements.Add(Element($"{container}\\{version}", "Formula_El_V", attrs.ToString()));
        return this;
    }

    /// <summary>Константа: визначення (раз) і значення в категорії <paramref name="category"/>.</summary>
    public AfXmlBuilder Constant(
        string m, string mv, string name, string value = "1", string category = "Common", string parameter = "p",
        string? unit = null)
    {
        var folder = FolderPath(m, mv, "Constants");
        EmitMethodology(m, mv);
        var definition = $"{folder}\\{name}";
        var nameIdx = 1 + m.Split('\\').Length - 1;
        if (_emitted.Add(definition))
        {
            var segs = definition.Split('\\').Length;
            _elements.Add(Element(definition, "Constant_El", Config("CInfo_Name", "%Element%")
                + Config("MInfo_Name", "%" + Ups(segs - 1 - nameIdx) + "Element%")
                + Config("MInfo_Version", "%" + Ups(segs - 2 - nameIdx) + "Element%")));
        }

        var valuePath = $"{definition}\\{category}\\V1";
        var vsegs = valuePath.Split('\\').Length;
        _elements.Add(Element(valuePath, "Constant_El_V", Value("CInfo_Value", value)
            + Value("CInfo_Parameter", parameter)
            + (unit is null ? string.Empty : Value("CInfo_Unit", unit))
            + Value("CInfo_StartDate", "2023-12-31T19:00:00Z")
            + Config("CInfo_Category", "%..\\Element%")
            + Config("MInfo_Name", "%" + Ups(vsegs - 1 - nameIdx) + "Element%")
            + Config("MInfo_Version", "%" + Ups(vsegs - 2 - nameIdx) + "Element%")));
        return this;
    }

    /// <summary>Довільний XML-фрагмент як дитина AFDatabase (для тестів толерантності).</summary>
    public AfXmlBuilder Raw(string xml)
    {
        _elements.Add(xml);
        return this;
    }

    private static string FolderPath(string m, string mv, string folder) => $"Methodologies\\{m}\\{mv}\\{folder}";

    /// <summary>Кореневі елементи методології й версії — з атрибутами, але без формул/констант (мають бути пропущені).</summary>
    private void EmitMethodology(string m, string mv)
    {
        if (_emitted.Add("root|Methodologies"))
        {
            _elements.Add(Element("Methodologies", "", Value("Display_link", "")).Replace("<Template></Template>", string.Empty, StringComparison.Ordinal));
        }

        if (_emitted.Add("m|" + m))
        {
            _elements.Add(Element($"Methodologies\\{m}", "Methodology", Value("Name", m)));
        }

        if (_emitted.Add($"v|{m}|{mv}"))
        {
            _elements.Add(Element($"Methodologies\\{m}\\{mv}", "Methodology_V", Value("StartDate", "2023-12-31T19:00:00Z")));
        }
    }

    public string Build(bool reverse = false)
    {
        var items = reverse ? Enumerable.Reverse(_elements) : _elements;
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
               + "<AF SchemaVersion=\"2.0\" ExportMode=\"AllReferences, DefaultValues, Security, Flat, SimplifiedConfigStrings\">"
               + "<AFSecurityIdentity><id>s</id><Name>Administrators</Name></AFSecurityIdentity>"
               + "<AFDatabase><id>d</id><Name>Test</Name><AFElementTemplate><id>t</id><Name>Formula_El</Name></AFElementTemplate>"
               + string.Concat(items)
               + "</AFDatabase></AF>";
    }

    public static Stream ToStream(string xml) => new MemoryStream(new UTF8Encoding(false).GetBytes(xml));
}
