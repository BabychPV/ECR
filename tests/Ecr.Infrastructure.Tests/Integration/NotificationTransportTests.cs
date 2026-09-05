// tests/Ecr.Infrastructure.Tests/Integration/NotificationTransportTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Integration;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// Транспорт сповіщень (<c>D-124</c>).
/// </summary>
/// <remarks>
/// ⛔ Найважливіше тут — **не втрачати повідомлення**. Недоступний SMTP має
/// лишити подію в черзі зі спробою і причиною, а не позначити «надіслано»:
/// другий варіант означає, що система вважає повідомленим того, хто нічого не
/// отримав.
///
/// ⚠ Пароль не бере участі в жодному тесті і не має: він живе за іменем
/// секрету (`ФВ-6.11`), і перевіряється саме те, що ім'я обов'язкове.
/// </remarks>
public sealed class NotificationTransportTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Без_Host_відправник_НЕ_налаштований()
    {
        // ⚠ «Не налаштовано» і «не доставлено» — різні стани. Перший лишає
        // чергу накопичуватися, другий рахує спробу; плутати їх означає ховати
        // перший за другим.
        var sender = Sender();

        Assert.False(sender.IsConfigured);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Із_Host_відправник_налаштований()
    {
        var sender = Sender(("Smtp:Host", "smtp.example.local"));

        Assert.True(sender.IsConfigured);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Ненелаштований_відправник_КИДАЄ_а_не_вдає_успіх()
    {
        // ⛔ Мовчазний успіх тут — найгірше з можливого: черга спорожніла б, а
        // листів не було б, і дізналися б про це, коли хтось не отримав
        // повідомлення про збій збору.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sender().SendAsync(["a@example.local"], "тема", "текст", CancellationToken.None));

        Assert.Contains("ECR_Smtp__Host", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожній_перелік_адресатів_відхиляється()
    {
        var sender = Sender(("Smtp:Host", "smtp.example.local"), ("Smtp:From", "ecr@example.local"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync([], "тема", "текст", CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Користувач_без_імені_секрету_є_помилкою_конфігурації()
    {
        // ⚠ Саме помилка, а не мовчазний перехід на анонімну відправку:
        // другий варіант тихо перетворив би автентифіковану пошту на відкриту.
        var sender = Sender(
            ("Smtp:Host", "smtp.example.local"),
            ("Smtp:From", "ecr@example.local"),
            ("Smtp:User", "ecr-noreply"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(["a@example.local"], "тема", "текст", CancellationToken.None));

        Assert.Contains("SecretName", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Недоступний_сервер_дає_ВИНЯТОК_щоб_подія_лишилася_в_черзі()
    {
        // ⛔ Головна перевірка `D-124`. Виняток мусить дійти до задачі: саме
        // вона рахує спробу і лишає запис у черзі (`ФВ-12.4a`). Проковтнути
        // його тут означало б «надіслано» для листа, якого немає.
        var sender = Sender(
            ("Smtp:Host", "127.0.0.1"),
            ("Smtp:Port", "2"),
            ("Smtp:From", "ecr@example.local"),
            ("Smtp:UseStartTls", "false"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => sender.SendAsync(["a@example.local"], "тема", "текст", CancellationToken.None));
    }

    private static SmtpNotificationSender Sender(params (string Key, string Value)[] settings)
    {
        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns((string?)null);

        return new SmtpNotificationSender(new InMemoryConfiguration(settings), secrets);
    }

    /// <summary>
    /// Мінімальна конфігурація на словнику.
    /// </summary>
    /// <remarks>
    /// ⚠ Замість `ConfigurationBuilder`: повний пакет конфігурації тягнувся б у
    /// тестову збірку заради одного індексатора, а відправник більше нічого й
    /// не читає. Нереалізовані члени кидають — щоб мовчазне `null` не видало
    /// себе за «налаштування відсутнє».
    /// </remarks>
    private sealed class InMemoryConfiguration(IEnumerable<(string Key, string Value)> values)
        : IConfiguration
    {
        private readonly Dictionary<string, string> _values =
            values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

        public string? this[string key]
        {
            get => _values.TryGetValue(key, out var value) ? value : null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => throw new NotSupportedException();

        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }
}
