using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// S11: скрипт розгортання майданчика ніколи не ставить згоду на незахищене
/// кільце DataProtection.
/// </summary>
/// <remarks>
/// ⛔ <c>Auth:DataProtection:AllowUnprotectedKeys</c> існує лише для
/// одноразових стендів (<c>smoke.ps1</c>, <c>e2e-stand.ps1</c>,
/// <c>setup-dev-db.ps1</c>): з ним Production стартує без сертифіката, і хто
/// читає базу чи її бекап, підробляє cookie сеансу будь-кого. Якби ключ
/// з'явився в <c>tools/deploy*</c>, кожен майданчик пішов би в цей режим
/// мовчки — Critical у журналі й Degraded у health легко пропустити.
///
/// ⚠ Перевіряється ТЕКСТ усіх файлів <c>tools/deploy*</c>, без винятку для
/// коментарів: пояснення «цей ключ тут не ставиться» писати іменем ключа
/// не треба — так сторож не зеленітиме на власному обході.
/// </remarks>
public sealed class DeployScriptNeverAllowsUnprotectedKeysTests
{
    private const string ConsentName = "AllowUnprotectedKeys";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_скриптах_розгортання_немає_згоди_на_незахищені_ключі()
    {
        var tools = Path.Combine(SourceTree.Root, "tools");
        var deploy = Directory.GetFiles(tools, "deploy*", SearchOption.TopDirectoryOnly);

        // ⛔ Самоперевірка: порожній перелік файлів дав би зелене саме тоді,
        // коли скрипт перейменували чи перенесли.
        Assert.Contains(deploy, f => Path.GetFileName(f) == "deploy-ecr.ps1");

        // Пошук живий: стенд цей рядок ставить, і сторож має його бачити.
        Assert.Contains(ConsentName, File.ReadAllText(Path.Combine(tools, "smoke.ps1")), StringComparison.Ordinal);

        var offenders = deploy
            .Where(f => File.ReadAllText(f).Contains(ConsentName, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Скрипт розгортання вмикає незахищене кільце DataProtection ({ConsentName}): "
            + string.Join(", ", offenders)
            + ". Майданчик — лише з сертифікатом (-DataProtectionThumbprint → "
            + "ECR_Auth__DataProtection__CertificateThumbprint).");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_розгортання_прокидає_відбиток_сертифіката_у_змінну_служби()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        Assert.Contains("DataProtectionThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("ECR_Auth__DataProtection__CertificateThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("HasPrivateKey", script, StringComparison.Ordinal);
        Assert.Contains(@"Cert:\LocalMachine\My", script, StringComparison.Ordinal);
    }
}
