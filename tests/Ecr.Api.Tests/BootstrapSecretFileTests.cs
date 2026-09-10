using Ecr.Api.Startup;
using Xunit;

namespace Ecr.Api.Tests;

public sealed class BootstrapSecretFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EcrBootstrapSecretTests_" + Guid.NewGuid());

    [Fact]
    public void Немає_файлу_повертає_null_без_помилки()
    {
        var result = BootstrapSecretFile.ReadAndDelete(_root);

        Assert.Null(result.Password);
        Assert.Null(result.DeleteError);
    }

    [Fact]
    public void Є_файл_повертає_пароль_і_видаляє_файл()
    {
        var path = BootstrapSecretFile.PathUnder(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Sup3r-Secret-P@ss\n");

        var result = BootstrapSecretFile.ReadAndDelete(_root);

        Assert.Equal("Sup3r-Secret-P@ss", result.Password);
        Assert.Null(result.DeleteError);
        Assert.False(File.Exists(path), "Файл мав зникнути одразу після читання.");
    }

    [Fact]
    public void Повторний_виклик_після_видалення_дає_null_а_не_старий_пароль()
    {
        var path = BootstrapSecretFile.PathUnder(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "one-time-value");

        BootstrapSecretFile.ReadAndDelete(_root);
        var second = BootstrapSecretFile.ReadAndDelete(_root);

        Assert.Null(second.Password);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
