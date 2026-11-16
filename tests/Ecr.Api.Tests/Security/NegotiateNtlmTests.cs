// tests/Ecr.Api.Tests/Security/NegotiateNtlmTests.cs
using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// PT-4 (пентест 2026-11; рішення людини 16.11): вхід Windows приймає лише Kerberos, доки
/// <c>Auth:Negotiate:AllowNtlm</c> не <c>true</c>. NTLM не прив'язаний до адресата й ретранслюється.
/// </summary>
/// <remarks>
/// Живу перевірку (Kerberos за іменем із SPN проходить, NTLM за IP — 401) може зробити лише майданчик із доменом:
/// Negotiate у TestServer не працює (Q-054). Тут — правило «усе, що не Kerberos, відхиляється» і ключ конфігурації.
/// </remarks>
public sealed class NegotiateNtlmTests
{
    [Theory]
    [InlineData("Kerberos", true)]
    [InlineData("kerberos", true)]
    [InlineData("NTLM", false)]
    [InlineData("Negotiate", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PT-4")]
    public void Лише_Kerberos_вважається_Kerberos(string? authenticationType, bool expected)
    {
        var identity = string.IsNullOrEmpty(authenticationType)
            ? new ClaimsIdentity()
            : new ClaimsIdentity(authenticationType);

        Assert.Equal(expected, AuthenticationSetup.IsKerberos(new ClaimsPrincipal(identity)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PT-4")]
    public void Без_суб_єкта_не_Kerberos_а_ключ_має_домовлене_ім_я()
    {
        Assert.False(AuthenticationSetup.IsKerberos(null));
        Assert.Equal("Auth:Negotiate:AllowNtlm", AuthenticationSetup.AllowNtlmKey);
    }
}
