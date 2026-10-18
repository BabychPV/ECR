using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Рішення людини 2026-10-01: allowlist <c>PiWebApi:AllowedHosts</c> у продукті ПОРОЖНІЙ.
/// Імена інтранет-хостів — дані замовника: їх задає адміністратор через
/// <c>appsettings.Production.json</c> або <c>ECR_PiWebApi__AllowedHosts__n</c>, а не зашиває дистрибутив.
/// Мутація: додати хост у масив <c>appsettings.json</c> / <c>appsettings.Production.json</c>, присвоїти
/// <c>ECR_PiWebApi__AllowedHosts__0</c> у <c>deploy-ecr.ps1</c> чи властивість у <c>.wxs</c> — тест червоніє (AU-11).
/// </summary>
public sealed class PiWebApiAllowedHostsDefaultTests
{
    private const string AppSettingsPath = "src/Ecr.Api/appsettings.json";

    /// <summary>Присвоєння значення ключу allowlist (змінна середовища, ключ конфігурації, властивість MSI).</summary>
    private static readonly Regex AssignmentRx = new(
        @"PiWebApi(__|:)AllowedHosts(__\d+)?[""']?\s*[=:]\s*[""']?[^\s""'\]\[]|Name=""ECR_PiWebApi__AllowedHosts|\bPIWEB\w*ALLOWED\w*HOSTS\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IntranetRx = new(
        @"\b(10\.\d+\.\d+\.\d+|192\.168\.\d+\.\d+|172\.(1[6-9]|2\d|3[01])\.\d+\.\d+)\b|\.(local|corp)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Abs(string rel) =>
        Path.Combine(SourceTree.Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string[] PiWebApiHosts(string json)
    {
        using var document = JsonDocument.Parse(json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("PiWebApi", out var piWebApi)
            || !piWebApi.TryGetProperty("AllowedHosts", out var hosts))
        {
            return Array.Empty<string>();
        }
        Assert.Equal(JsonValueKind.Array, hosts.ValueKind);
        return hosts.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
    }

    private static string StripComments(string path, string text)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".ps1")
        {
            text = Regex.Replace(text, @"<#[\s\S]*?#>", "");
            return Regex.Replace(text, @"^[ \t]*#.*$", "", RegexOptions.Multiline);
        }
        if (ext is ".wxs" or ".wxl")
        {
            return Regex.Replace(text, @"<!--[\s\S]*?-->", "");
        }
        if (ext == ".cs")
        {
            text = Regex.Replace(text, @"/\*[\s\S]*?\*/", "");
            return Regex.Replace(text, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
        }
        return text;
    }

    [Fact]
    public void Дефолтний_PiWebApi_AllowedHosts_порожній_і_без_інтранет_імен()
    {
        var path = Path.Combine(SourceTree.Root, AppSettingsPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        Assert.True(document.RootElement.TryGetProperty("PiWebApi", out var piWebApi), "немає секції PiWebApi");
        Assert.True(piWebApi.TryGetProperty("AllowedHosts", out var hosts), "немає ключа PiWebApi:AllowedHosts");
        Assert.Equal(JsonValueKind.Array, hosts.ValueKind);

        var values = hosts.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        Assert.True(values.Length == 0,
            "PiWebApi:AllowedHosts за замовчуванням має бути порожнім (хости — дані замовника), знайдено: "
            + string.Join(", ", values));
        foreach (var v in values)
        {
            Assert.DoesNotContain("ncat", v, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".corp", v, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".local", v, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("src/Ecr.Api/appsettings.json", true)]
    [InlineData("src/Ecr.Api/appsettings.Production.json", false)]
    [InlineData("src/Ecr.Api/appsettings.Development.json", false)]
    public void Розповсюджуваний_appsettings_не_містить_хостів_PiWebApi(string rel, bool sectionRequired)
    {
        var json = File.ReadAllText(Abs(rel));
        if (sectionRequired)
        {
            using var document = JsonDocument.Parse(json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Assert.True(document.RootElement.TryGetProperty("PiWebApi", out var s)
                        && s.TryGetProperty("AllowedHosts", out _),
                $"{rel}: немає ключа PiWebApi:AllowedHosts");
        }
        var values = PiWebApiHosts(json);
        Assert.True(values.Length == 0,
            $"{rel}: PiWebApi:AllowedHosts має бути порожнім (хости — дані замовника), знайдено: "
            + string.Join(", ", values));
    }

    public static IEnumerable<object[]> DistributionTextFiles()
    {
        var files = new List<string> { "tools/deploy-ecr.ps1" };
        files.AddRange(Directory.GetFiles(Abs("installer/Ecr.Installer"), "*.wxs")
            .Concat(Directory.GetFiles(Abs("installer/Ecr.Installer"), "Strings.wxl"))
            .Select(p => Path.GetRelativePath(SourceTree.Root, p).Replace('\\', '/')));
        files.AddRange(Directory.GetFiles(Abs("tools/Ecr.Setup"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => Path.GetRelativePath(SourceTree.Root, p).Replace('\\', '/')));
        return files.Select(f => new object[] { f });
    }

    [Theory]
    [MemberData(nameof(DistributionTextFiles))]
    public void Деплой_скрипти_і_інсталятор_не_присвоюють_PiWebApi_AllowedHosts(string rel)
    {
        var text = StripComments(rel, File.ReadAllText(Abs(rel)));
        var hit = AssignmentRx.Match(text);
        Assert.False(hit.Success,
            $"{rel}: дистрибутив не повинен задавати PiWebApi:AllowedHosts (хости — дані замовника), знайдено: «{hit.Value}»");
    }

    [Theory]
    [InlineData("src/Ecr.Api/appsettings.json")]
    [InlineData("src/Ecr.Api/appsettings.Production.json")]
    [InlineData("src/Ecr.Api/appsettings.Development.json")]
    [InlineData("installer/Ecr.Installer/Package.wxs")]
    [InlineData("installer/Ecr.Installer/Service.wxs")]
    [InlineData("installer/Ecr.Installer/Worker.wxs")]
    [InlineData("installer/Ecr.Installer/Folders.wxs")]
    [InlineData("installer/Ecr.Installer/Strings.wxl")]
    public void Розповсюджувані_конфіги_й_інсталятор_без_інтранет_хостів(string rel)
    {
        var text = StripComments(rel, File.ReadAllText(Abs(rel)));
        var hit = IntranetRx.Match(text);
        Assert.False(hit.Success, $"{rel}: інтранет-хост/суфікс у дистрибутиві (дані замовника): «{hit.Value}»");
    }

    [Fact]
    public void Позитивний_контроль_сканів_ловить_синтетичні_порушення()
    {
        // Без цього зелений сторож міг би бути хибнозеленим (регулярка/парсер нічого не бачить).
        Assert.Single(PiWebApiHosts("""{ "PiWebApi": { "AllowedHosts": ["x"] } }"""));
        Assert.Empty(PiWebApiHosts("""{ "PiWebApi": { "AllowedHosts": [] } }"""));
        Assert.Empty(PiWebApiHosts("""{ "Other": 1 }"""));

        Assert.Matches(AssignmentRx, "ECR_PiWebApi__AllowedHosts__0=pi");
        Assert.Matches(AssignmentRx, "$env:ECR_PiWebApi__AllowedHosts__0 = 'x'");
        Assert.Matches(AssignmentRx, "\"PiWebApi:AllowedHosts\": \"x\"");
        Assert.Matches(AssignmentRx, "<Property Id=\"P\" Name=\"ECR_PiWebApi__AllowedHosts__0\" />");
        Assert.Matches(AssignmentRx, "<Property Id=\"PIWEB_ALLOWED_HOSTS\" Value=\"x\" />");
        Assert.DoesNotMatch(AssignmentRx, "# ECR_PiWebApi__AllowedHosts__* — лише згадка");

        Assert.Equal("", StripComments("a.ps1", "# ECR_PiWebApi__AllowedHosts__0=pi").Trim());
        Assert.Equal("", StripComments("a.ps1", "<# ECR_PiWebApi__AllowedHosts__0=pi #>").Trim());
        Assert.Equal("", StripComments("a.wxs", "<!-- ECR_PiWebApi__AllowedHosts__0=pi -->").Trim());

        Assert.Matches(IntranetRx, "10.1.2.3");
        Assert.Matches(IntranetRx, "192.168.0.5");
        Assert.Matches(IntranetRx, "172.16.4.4");
        Assert.Matches(IntranetRx, "pi.ncat.corp");
        Assert.Matches(IntranetRx, "host.local");
        Assert.DoesNotMatch(IntranetRx, "127.0.0.1");
    }
}
