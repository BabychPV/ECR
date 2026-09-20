// tests/Ecr.Domain.Tests/Notifications/NotificationEntitiesTests.cs
using Ecr.Domain.Entities.Notifications;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Notifications;

/// <summary>Сутності сповіщень (<c>BE-32</c>): те, що не залежить від бази.</summary>
public sealed class NotificationEntitiesTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Канал_створюється_ввімкненим_без_секрету_і_порожній_блоб_секретом_не_є()
    {
        var channel = new NotificationChannel(NotificationChannelKind.Smtp, "Ops", "{}", Now, byUserId: 7);

        Assert.True(channel.IsEnabled);
        Assert.False(channel.HasSecret);

        channel.ReplaceSecret([1, 2], Now.AddMinutes(1), byUserId: 8);
        Assert.True(channel.HasSecret);
        Assert.Equal(8, channel.ModifiedByUserId);
        Assert.Equal(Now.AddMinutes(1), channel.ModifiedAt);

        // Порожній масив — «секрет прибрано», а не «секрет нульової довжини»:
        // інакше `hasSecret` казав би «є» про канал, якому нічим підписатися.
        channel.ReplaceSecret([], Now, byUserId: 8);
        Assert.False(channel.HasSecret);
        Assert.Null(channel.SecretProtected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Назва_каналу_обовязкова_і_не_довша_за_стовпець()
    {
        Assert.Throws<ArgumentException>(
            () => new NotificationChannel(NotificationChannelKind.Smtp, " ", "{}", Now, null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NotificationChannel(NotificationChannelKind.Smtp, new string('n', 101), "{}", Now, null));

        _ = new NotificationChannel(NotificationChannelKind.Smtp, new string('n', 100), "{}", Now, null);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(NotificationSeverity.Info, false)]
    [InlineData(NotificationSeverity.Warning, true)]
    [InlineData(NotificationSeverity.Error, true)]
    public void Правило_пропускає_події_не_нижчі_за_свою_межу(NotificationSeverity severity, bool expected)
    {
        var rule = new NotificationRule(NotificationEventKind.JobFailed, channelId: 1, NotificationSeverity.Warning);

        Assert.Equal(expected, rule.Matches(severity));

        rule.Update(NotificationSeverity.Warning, isEnabled: false);
        Assert.False(rule.Matches(severity));
    }
}
