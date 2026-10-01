using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Рішення людини 2026-10-01: allowlist <c>PiWebApi:AllowedHosts</c> у продукті ПОРОЖНІЙ.
/// Імена інтранет-хостів — дані замовника: їх задає адміністратор через
/// <c>appsettings.Production.json</c> або <c>ECR_PiWebApi__AllowedHosts__n</c>, а не зашиває дистрибутив.
/// Мутація: додати будь-який хост у масив <c>appsettings.json</c> — тест червоніє.
/// </summary>
public sealed class PiWebApiAllowedHostsDefaultTests
{
    private const string AppSettingsPath = "src/Ecr.Api/appsettings.json";

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
}
