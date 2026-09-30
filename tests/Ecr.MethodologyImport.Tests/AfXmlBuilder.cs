using System.Security;
using System.Text;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// Синтетичний AF XML за структурою, виведеною з документів (НЕ копія ECR_01_Air.xml):
/// <c>Methodologies\&lt;М&gt;\&lt;ВерсіяМ&gt;\Formulas\&lt;Ф&gt;\&lt;ВерсіяФ&gt;</c> і
/// <c>Methodologies\&lt;М&gt;\&lt;ВерсіяМ&gt;\Constants\&lt;К&gt;</c>, атрибути <c>FInfo_*</c>/<c>CInfo_*</c>/<c>MInfo_*</c>.
/// </summary>
internal sealed class AfXmlBuilder
{
    private readonly List<(string Methodology, string Version, string Xml)> _items = [];

    public static string Attr(string name, string? value = null, string? config = null)
    {
        var sb = new StringBuilder("<AFAttribute><Name>").Append(SecurityElement.Escape(name)).Append("</Name>");
        if (value is not null)
        {
            sb.Append("<Value>").Append(SecurityElement.Escape(value)).Append("</Value>");
        }

        if (config is not null)
        {
            sb.Append("<ConfigString>").Append(SecurityElement.Escape(config)).Append("</ConfigString>");
        }

        return sb.Append("</AFAttribute>").ToString();
    }

    /// <summary>Формула методології <paramref name="m"/>/<paramref name="mv"/>; поля передаються «як у AF» (з пробілами).</summary>
    public AfXmlBuilder Formula(
        string m, string mv, string name, string args, string text = "1", string version = "V1", string? extra = null)
    {
        var attrs = new StringBuilder()
            .Append(Attr("FInfo_Name", name))
            .Append(Attr("FInfo_Version", version))
            .Append(Attr("FInfo_Arguments", args))
            .Append(Attr("FInfo_Text", text))
            .Append(Attr("FInfo_StartDate", "2023-12-31T19:00:00Z"))
            .Append(Attr("FInfo_EndDate", "9999-12-31T00:00:00Z"))
            .Append(Attr("MInfo_Name", m))
            .Append(Attr("MInfo_Version", mv))
            .Append(extra);
        var xml = $"<AFElement><Name>{SecurityElement.Escape(name)}</Name>"
                  + $"<AFElement><Name>{SecurityElement.Escape(version)}</Name>{attrs}</AFElement></AFElement>";
        _items.Add((m, mv, "Formulas|" + xml));
        return this;
    }

    public AfXmlBuilder Constant(string m, string mv, string name, string value = "1")
    {
        var attrs = new StringBuilder()
            .Append(Attr("CInfo_Name", name))
            .Append(Attr("CInfo_Value", value))
            .Append(Attr("CInfo_Version", mv))
            .Append(Attr("MInfo_Name", m))
            .Append(Attr("MInfo_Version", mv));
        _items.Add((m, mv, $"Constants|<AFElement><Name>{SecurityElement.Escape(name)}</Name>{attrs}</AFElement>"));
        return this;
    }

    /// <summary>Довільний XML-фрагмент як дитина кореня бази (для тестів толерантності).</summary>
    public AfXmlBuilder Raw(string xml)
    {
        _items.Add(("", "", "Raw|" + xml));
        return this;
    }

    public string Build(bool reverse = false)
    {
        var items = reverse ? Enumerable.Reverse(_items).ToList() : _items;
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<AFDatabase><Name>Test</Name>");
        sb.Append("<AFElement><Name>Methodologies</Name>");
        foreach (var byMethodology in items.Where(i => i.Methodology.Length > 0).GroupBy(i => i.Methodology))
        {
            sb.Append("<AFElement><Name>").Append(SecurityElement.Escape(byMethodology.Key)).Append("</Name>");
            foreach (var byVersion in byMethodology.GroupBy(i => i.Version))
            {
                sb.Append("<AFElement><Name>").Append(SecurityElement.Escape(byVersion.Key)).Append("</Name>");
                foreach (var byFolder in byVersion.GroupBy(i => i.Xml[..i.Xml.IndexOf('|', StringComparison.Ordinal)]))
                {
                    sb.Append("<AFElement><Name>").Append(byFolder.Key).Append("</Name>");
                    foreach (var item in byFolder)
                    {
                        sb.Append(item.Xml[(item.Xml.IndexOf('|', StringComparison.Ordinal) + 1)..]);
                    }

                    sb.Append("</AFElement>");
                }

                sb.Append("</AFElement>");
            }

            sb.Append("</AFElement>");
        }

        sb.Append("</AFElement>");
        foreach (var raw in items.Where(i => i.Methodology.Length == 0))
        {
            sb.Append(raw.Xml[(raw.Xml.IndexOf('|', StringComparison.Ordinal) + 1)..]);
        }

        return sb.Append("</AFDatabase>").ToString();
    }

    public static Stream ToStream(string xml) => new MemoryStream(new UTF8Encoding(false).GetBytes(xml));
}
