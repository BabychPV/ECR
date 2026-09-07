using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecr.TestKit;

/// <summary>
/// Нормалізація документа OpenAPI для порівняння зі знімком (<c>D-136</c>).
/// </summary>
/// <remarks>
/// ⛔ Нормалізація обов'язкова, інакше тест «моргає»: генератор не обіцяє
/// порядку ключів, а версія збірки й дата змінюються від перезбирання. Тест,
/// який падає через раз, вимикають — і разом із ним зникає єдине, що звіряє
/// контракт із кодом.
///
/// ⚠ Знімок ловить `A7-35` у мить зміни коду відповіді: сервер віддавав
/// <c>204</c>, а контракт обіцяв <c>200</c>, і розбіжність жила в прозі, бо
/// звіряти її було нічим.
/// </remarks>
public static class OpenApiSnapshot
{
    /// <summary>
    /// Поля, що змінюються від перезбирання і до контракту не належать.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>version</c> прибирається лише на рівні <c>info</c>: усередині схем
    /// це може бути звичайне поле DTO, і прибрати його там означало б
    /// перестати помічати його зникнення.
    /// </remarks>
    private static readonly string[] VolatileInfoFields = ["version"];

    /// <summary>Приводить документ до канонічного вигляду.</summary>
    /// <param name="json">Документ, як його віддає <c>/openapi/v1.json</c>.</param>
    public static string Normalize(string json)
    {
        var node = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Документ OpenAPI порожній.");

        if (node["info"] is JsonObject info)
        {
            foreach (var field in VolatileInfoFields)
            {
                info.Remove(field);
            }
        }

        var canonical = Sort(node);

        // ⚠ Два пробіли і LF — щоб діф у рев'ю читався рядками, а не одним
        // рядком на весь файл, і щоб знімок не змінювався від налаштувань git
        // на іншій машині.
        var text = canonical.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        return text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>
    /// Упорядковує ключі об'єктів лексикографічно, рекурсивно.
    /// </summary>
    /// <remarks>
    /// ⛔ Порядок МАСИВІВ не чіпається: у OpenAPI він значущий (параметри,
    /// <c>enum</c>, <c>required</c>), і сортування приховало б справжню зміну
    /// контракту.
    /// </remarks>
    private static JsonNode Sort(JsonNode node)
    {
        switch (node)
        {
            case JsonObject source:
                var sorted = new JsonObject();
                foreach (var pair in source.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = pair.Value is null ? null : Sort(pair.Value.DeepClone());
                }

                return sorted;

            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(item is null ? null : Sort(item.DeepClone()));
                }

                return items;

            // ⛔ Переноси рядків ВСЕРЕДИНІ значень теж зводяться до самого
            // LF, і це не косметика. Описи в документі приходять із
            // XML-коментарів вихідних файлів, а ті на Windows мають CRLF: у
            // JSON пара CR+LF лежить ЕКРАНОВАНОЮ, тобто чотирма звичайними
            // символами тексту. Заміна переносів у серіалізованому документі
            // їх не бачить — там немає жодного справжнього переносу.
            //
            // ⚠ Наслідок: знімок, знятий на Windows, не міг збігтися з
            // документом, зібраним на Linux, ЖОДНОГО разу — контракт залежав
            // від того, хто його зібрав. Знайшов це перший прогін конвеєра;
            // локально тест був зелений завжди, бо там обидві сторони CRLF.
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(
                    text.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal));

            default:
                return node.DeepClone();
        }
    }

    /// <summary>Шлях до знімка в репозиторії.</summary>
    /// <remarks>
    /// ⚠ Поза <c>docs/</c>: каталог документації накритий <c>CHECKSUMS.txt</c>,
    /// і згенеровані файли там ламали б звірку сум (<c>D-135</c>).
    /// </remarks>
    public static string Path()
        => System.IO.Path.Combine(SolutionRoot(), "contracts", "openapi.snapshot.json");

    /// <summary>Читає знімок; кидає з поясненням, якщо його немає.</summary>
    public static string Read()
    {
        var path = Path();

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Немає знімка OpenAPI: {path}. Створити — " +
                "запустити цей тест із ECR_UPDATE_SNAPSHOT=1.",
                path);
        }

        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Перезаписує знімок; викликається лише за явним прапорцем.</summary>
    public static void Write(string normalized)
    {
        var path = Path();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // ⚠ UTF-8 БЕЗ BOM і з LF: знімок читають і люди в рев'ю, і `git diff`
        // на трьох різних машинах. BOM перетворив би перший рядок на
        // «змінений» у половині з них.
        File.WriteAllText(path, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Не знайдено кореня рішення (Ecr.sln).");
    }
}
