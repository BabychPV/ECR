// tests/Ecr.Domain.Tests/Notifications/SmtpSettingsTests.cs
using Ecr.Domain.Entities.Notifications;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Notifications;

/// <summary>
/// Сутність налаштувань SMTP (<c>D-263</c>) без бази: межі адреси, повнота, пароль і автор зміни.
/// </summary>
/// <remarks>
/// ⚠ Набір закриває мутації, що вижили після тестів обробника (Stryker, лінія mutation-new-code-2): обробник
/// сам нормалізує пароль і перевіряє поля, тож підміна тих самих правил у сутності лишалася непоміченою.
/// </remarks>
public sealed class SmtpSettingsTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Рівно 254 символи: 64 у локальній частині, мітки домену по 63, 63 і 61.</summary>
    private static readonly string Longest =
        new string('a', 64) + "@" + new string('x', 63) + "." + new string('y', 63) + "." + new string('z', 61);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    [InlineData("a@corp.example", true)]        // локальна частина з одного символу
    [InlineData("ops@x.example", true)]         // мітка домену з одного символу
    [InlineData("ops@corp..example", false)]    // порожня мітка
    [InlineData("@corp.example", false)]
    public void Адреса_приймає_однобуквені_частини_і_відхиляє_порожні(string address, bool expected)
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (IsValidAddress): `at < 1` → `at <= 1`, `label.Length is >= 1` → `> 1` —
        // відхилили б звичайну адресу з однобуквеною частиною.
        Assert.Equal(expected, SmtpSettings.IsValidAddress(address));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public void Адреса_рівно_254_символи_приймається_255_ні()
    {
        Assert.Equal(254, Longest.Length);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `Length > AddressMaxLength` → `>=` відхилив би 254; `||` → `&&` пропустив би 255.
        Assert.True(SmtpSettings.IsValidAddress(Longest));
        Assert.False(SmtpSettings.IsValidAddress(Longest + "z"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public void Повнота_вимагає_хоста_й_відправника_а_не_лише_ввімкнення()
    {
        var settings = new SmtpSettings(Now, byUserId: 1);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `Host.Length > 0` / `FromAddress.Length > 0` → `>= 0` назвали б повною чернетку
        // без хоста чи без відправника — і транспорт брався б із неї.
        settings.Update(string.Empty, 587, SmtpEncryptionMode.StartTls, "ecr@corp.example", null, SmtpAuthMode.None, null, true, Now, 1);
        Assert.False(settings.IsComplete);

        settings.Update("smtp.corp.example", 587, SmtpEncryptionMode.StartTls, string.Empty, null, SmtpAuthMode.None, null, true, Now, 1);
        Assert.False(settings.IsComplete);

        settings.Update("smtp.corp.example", 587, SmtpEncryptionMode.StartTls, "ecr@corp.example", null, SmtpAuthMode.None, null, true, Now, 1);
        Assert.True(settings.IsComplete);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public void Режим_без_автентифікації_прибирає_пароль_і_логін_а_порожній_блоб_паролем_не_є()
    {
        var settings = new SmtpSettings(Now, byUserId: 1);
        settings.ReplacePassword([1, 2, 3], Now, byUserId: 1);
        Assert.True(settings.HasPassword);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: прибрати блок `if (auth == None) PasswordProtected = null`, `&&` → `||` в UserName.
        settings.Update("smtp.corp.example", 25, SmtpEncryptionMode.None, "ecr@corp.example", null, SmtpAuthMode.None, "mailer", true, Now, 1);
        Assert.False(settings.HasPassword);
        Assert.Null(settings.PasswordProtected);
        Assert.Null(settings.UserName);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `protectedPassword is { Length: > 0 } ? … : null` → завжди блоб або `>= 0` —
        // зберігся б порожній масив замість «пароля немає».
        settings.ReplacePassword([], Now, byUserId: 1);
        Assert.Null(settings.PasswordProtected);
        Assert.False(settings.HasPassword);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public void Кожна_зміна_фіксує_момент_і_автора()
    {
        var settings = new SmtpSettings(Now, byUserId: 3);
        Assert.Equal((Now, (int?)3), (settings.UpdatedAt, settings.UpdatedByUserId));

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: прибрати `Touch(...)` у конструкторі, Update чи ReplacePassword — у журналі
        // налаштувань лишився б попередній автор.
        settings.Update("smtp.corp.example", 587, SmtpEncryptionMode.StartTls, "ecr@corp.example", null, SmtpAuthMode.None, null, false, Now.AddMinutes(1), 4);
        Assert.Equal((Now.AddMinutes(1), (int?)4), (settings.UpdatedAt, settings.UpdatedByUserId));

        settings.ReplacePassword([9], Now.AddMinutes(2), byUserId: 5);
        Assert.Equal((Now.AddMinutes(2), (int?)5), (settings.UpdatedAt, settings.UpdatedByUserId));
    }
}
