using Ecr.Api.Startup;
using Xunit;

namespace Ecr.Api.Tests;

public sealed class BootstrapSecretFileTests : IDisposable
{
    // Механіку читання й видалення перевіряємо з довіреним власником: на
    // Windows-машині розробника без підвищення власник файлу тесту — сам
    // користувач, і типова перевірка (L10-02) його слушно відхилила б.
    private static readonly Func<string, string?> TrustedOwner = _ => null;

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

        var result = BootstrapSecretFile.ReadAndDelete(_root, TrustedOwner);

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

        BootstrapSecretFile.ReadAndDelete(_root, TrustedOwner);
        var second = BootstrapSecretFile.ReadAndDelete(_root, TrustedOwner);

        Assert.Null(second.Password);
    }

    [Fact]
    public void Файл_з_чужим_власником_відхиляється_пароль_не_повертається_файл_видаляється()
    {
        // L10-02: файл підклав не адміністратор — пароль атакувальника не має
        // стати паролем першого адміністратора.
        var path = BootstrapSecretFile.PathUnder(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "attacker-chosen");

        var result = BootstrapSecretFile.ReadAndDelete(_root, _ => "S-1-5-21-1-2-3-1001");

        Assert.Null(result.Password);
        Assert.Equal("S-1-5-21-1-2-3-1001", result.RejectedOwner);
        Assert.False(File.Exists(path), "Підкладений файл мав бути видалений.");
    }

    [Fact]
    public void Довірений_власник_файл_приймається()
    {
        var path = BootstrapSecretFile.PathUnder(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "admin-chosen");

        var result = BootstrapSecretFile.ReadAndDelete(_root, TrustedOwner);

        Assert.Equal("admin-chosen", result.Password);
        Assert.Null(result.RejectedOwner);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
