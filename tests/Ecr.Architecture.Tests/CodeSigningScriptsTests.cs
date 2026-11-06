using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// CL-3 (рішення людини 06.11, Q-6 «A+B зараз, потім C»): самопідписаний підпис <c>Ecr.msi</c> і SHA-256 у release
/// notes. Сторож контракту скриптів видачі: сертифікат саме для підпису коду, ключ типово не експортується, пароль
/// резервної копії — лише SecureString, підпис — SHA-256 (<c>signtool /fd SHA256</c>), хеш переписується ПІСЛЯ
/// підпису, документ описує розповсюдження <c>.cer</c> (GPO/certutil).
/// </summary>
/// <remarks>
/// Статичний (як <c>LandPostCheckPackageTests</c>): модуль PKI є лише у Windows, а CI — Linux. Мутації (прогнано):
/// <c>NonExportable</c> → <c>Exportable</c> за замовчуванням; <c>[securestring] $PfxPassword</c> → <c>[string]</c>;
/// прибрати <c>'/fd', 'SHA256'</c> у гілці <c>-Thumbprint</c> — кожна червоніє.
/// </remarks>
public sealed class CodeSigningScriptsTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([SourceTree.Root, .. parts]));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сертифікат_для_підпису_коду_ключ_типово_неекспортований_пароль_лише_SecureString()
    {
        var script = Read("tools", "new-codesign-cert.ps1");

        Assert.Contains("-Type CodeSigningCert", script, StringComparison.Ordinal);
        Assert.Contains("-HashAlgorithm SHA256", script, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\$exportPolicy = if \(\$BackupPfxPath\) \{ 'Exportable' \} else \{ 'NonExportable' \}"), script);
        Assert.Matches(new Regex(@"\[securestring\]\s*\$PfxPassword", RegexOptions.IgnoreCase), script);
        Assert.DoesNotMatch(new Regex(@"ConvertTo-SecureString\s+.*-AsPlainText", RegexOptions.IgnoreCase), script);
        Assert.Contains("Export-Certificate -Cert $cert -FilePath $CerPath -Type CERT", script, StringComparison.Ordinal);
        Assert.Contains("SupportsShouldProcess", script, StringComparison.Ordinal);
        Assert.Contains("certutil -addstore TrustedPublisher", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Підпис_за_відбитком_SHA256_через_signtool_і_хеш_після_підпису()
    {
        var script = Read("tools", "sign-msi.ps1");

        Assert.Contains("@('sign', '/sha1', $Thumbprint, '/fd', 'SHA256')", script, StringComparison.Ordinal);
        Assert.Contains("HashAlgorithm = 'SHA256'", script, StringComparison.Ordinal);
        Assert.Contains("signtool sign /fd SHA256", script, StringComparison.Ordinal);

        // Хеш рахується ПІСЛЯ підпису (останні рядки скрипта): у release notes — хеш підписаного файла.
        var sign = script.IndexOf("& signtool @toolArgs", StringComparison.Ordinal);
        var hash = script.LastIndexOf("Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256", StringComparison.Ordinal);
        Assert.True(sign > 0 && hash > sign, "sign-msi.ps1: .sha256 має рахуватися після підпису");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Документ_фіксує_рішення_AB_і_розповсюдження_cer()
    {
        var doc = Read("docs", "build", "MSI-SIGNING-OPTIONS.md");

        Assert.Contains("A+B зараз, потім C", doc, StringComparison.Ordinal);
        Assert.Contains("tools/new-codesign-cert.ps1", doc, StringComparison.Ordinal);
        Assert.Contains("certutil -addstore TrustedPublisher", doc, StringComparison.Ordinal);
        Assert.Contains("certutil -addstore Root", doc, StringComparison.Ordinal);
        Assert.Contains("verify-msi.ps1 -MsiPath .\\Ecr.msi -IntegrityOnly -RequireSignature -TrustedThumbprint", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("поки варіант не обрано, **MSI не підписується**", doc, StringComparison.Ordinal);
    }
}
