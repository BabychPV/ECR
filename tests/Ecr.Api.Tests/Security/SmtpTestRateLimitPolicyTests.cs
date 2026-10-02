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
