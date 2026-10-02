// tests/Ecr.Api.Tests/Security/SmtpTestRateLimitPolicyTests.cs

using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Ecr.Api.Auth;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>Політика проб транспорту без бази: дефолти без ключів, розділи користувача й анонімів, валідація ключів.</summary>
public sealed class SmtpTestRateLimitPolicyTests
{
    private static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    /// <summary>Годинник із ручним кроком (пакета fake-годинника в тестах немає).</summary>
    private sealed class StepTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Без_ключів_конфігурації_діють_дефолти_5_на_хвилину_і_30_на_годину()
    {
        var policy = new SmtpTestRateLimitPolicy(Empty);
        var partition = policy.GetPartition(ContextOf("1"));
        using var limiter = partition.Factory(partition.PartitionKey);

        for (var i = 0; i < 5; i++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }

        using var sixth = limiter.AttemptAcquire();
        Assert.False(sixth.IsAcquired);

        using var quota = new SmtpTestSystemQuota(Empty);

        for (var i = 0; i < 30; i++)
        {
            using var lease = quota.TryAcquire();
            Assert.True(lease.IsAcquired);
        }

        using var over = quota.TryAcquire();
        Assert.False(over.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Повернений_токен_квоти_доступний_знову_лише_у_тому_самому_вікні_і_лише_раз()
    {
        var time = new StepTime();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [SmtpTestRateLimitPolicy.SystemPermitKey] = "1" }).Build();
        using var quota = new SmtpTestSystemQuota(cfg, time);

        var first = quota.TryAcquire();
        Assert.True(first.IsAcquired);
        Assert.False(quota.TryAcquire().IsAcquired);

        // Мутація: прибрати `_used--` у `Refund` → повернення не діє.
        first.Refund();
        first.Refund(); // повторне повернення нічого не додає
        var second = quota.TryAcquire();
        Assert.True(second.IsAcquired);
        Assert.False(quota.TryAcquire().IsAcquired);

        // Нове вікно: токен зі старого вікна не повертається.
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var third = quota.TryAcquire();
        Assert.True(third.IsAcquired);
        second.Refund();
        Assert.False(quota.TryAcquire().IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Розділ_анонімів_окремий_від_користувачів_і_залежить_від_адреси()
    {
        var policy = new SmtpTestRateLimitPolicy(Empty);

        var user = policy.GetPartition(ContextOf("1")).PartitionKey;
        var anonA = policy.GetPartition(ContextOf(null, "10.0.0.1")).PartitionKey;
        var anonB = policy.GetPartition(ContextOf(null, "10.0.0.2")).PartitionKey;
        var anonNoId = policy.GetPartition(ContextOf(string.Empty, "10.0.0.1")).PartitionKey;

        Assert.NotEqual(user, anonA);
        Assert.NotEqual(anonA, anonB);
        Assert.Equal(anonA, anonNoId);
        Assert.NotEqual(user, policy.GetPartition(ContextOf("2")).PartitionKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Конфігурація_без_ключів_SmtpTest_проходить_валідацію_а_нуль_ні()
    {
        Assert.Empty(EcrConfigurationValidation.Validate(Empty));

        foreach (var key in new[] { SmtpTestRateLimitPolicy.PermitKey, SmtpTestRateLimitPolicy.SystemPermitKey })
        {
            var bad = new ConfigurationBuilder().AddInMemoryCollection([new(key, "0")]).Build();
            Assert.Single(EcrConfigurationValidation.Validate(bad));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Межа_користувача_береться_з_конфігурації_вікно_хвилина_без_черги_а_системна_година()
    {
        var two = new ConfigurationBuilder()
            .AddInMemoryCollection([new(SmtpTestRateLimitPolicy.PermitKey, "2"), new(SmtpTestRateLimitPolicy.SystemPermitKey, "3")])
            .Build();

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: ігнорувати PermitKey/SystemPermitKey (завжди 5/30), Window хвилина → година
        // (і навпаки для системної квоти), QueueLimit 0 → 1 — червоніє відповідний рядок.
        var partition = new SmtpTestRateLimitPolicy(two).GetPartition(ContextOf("1"));
        using var limiter = partition.Factory(partition.PartitionKey);

        using (var first = limiter.AttemptAcquire())
        using (var second = limiter.AttemptAcquire())
        {
            Assert.True(first.IsAcquired && second.IsAcquired);
        }

        // Третя — одразу відмова, а не очікування в черзі (UseRateLimiter кличе AcquireAsync).
        var third = limiter.AcquireAsync().AsTask();
        Assert.True(third.IsCompleted);
        using (var rejected = await third)
        {
            Assert.False(rejected.IsAcquired);
        }

        // ⚠ Поповнює розділ сам PartitionedRateLimiter (фабрика розділу знімає AutoReplenishment) — тож
        // тут лише період вікна.
        var replenishing = Assert.IsAssignableFrom<ReplenishingRateLimiter>(limiter);
        Assert.Equal(TimeSpan.FromMinutes(1), replenishing.ReplenishmentPeriod);

        using var quota = new SmtpTestSystemQuota(two);
        for (var i = 0; i < 3; i++)
        {
            using var lease = quota.TryAcquire();
            Assert.True(lease.IsAcquired);
        }

        using (var over = quota.TryAcquire())
        {
            Assert.False(over.IsAcquired);
        }

        var system = (ReplenishingRateLimiter)typeof(SmtpTestSystemQuota)
            .GetField("_limiter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(quota)!;
        Assert.Equal(TimeSpan.FromHours(1), system.ReplenishmentPeriod);
        Assert.True(system.IsAutoReplenishing);
    }

    private static DefaultHttpContext ContextOf(string? userId, string? ip = null)
    {
        var context = new DefaultHttpContext();

        if (userId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                userId.Length > 0 ? [new Claim(AuthenticationSetup.UserIdClaim, userId)] : [], "test"));
        }

        if (ip is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        }

        return context;
    }
}
