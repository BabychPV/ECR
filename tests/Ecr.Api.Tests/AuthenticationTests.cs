using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Два провайдери, одна cookie (ФВ-6.1) і негайна дія відкликання прав.
/// </summary>
public sealed class AuthenticationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Локальний_вхід_видає_ту_саму_cookie_що_й_доменний()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Невірний_пароль_і_неіснуючий_користувач_дають_однакову_відповідь()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_N_невдалих_спроб_обліковий_запис_блокується()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_ролей_робить_поточну_сесію_недійсною_негайно()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пароль_не_зустрічається_у_логах_трасуванні_і_відповідях()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Анонімний_запит_до_захищеного_ендпоінта_дає_401_а_не_редирект()
        => Assert.Fail("not implemented");
}
