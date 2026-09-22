using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>Чернетка опису довідника (<c>BE-24</c> крок 2).</summary>
public sealed class RegistryDefinitionDraftTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [Trait("Directive", "BE-24")]
    [InlineData("")]
    [InlineData("   ")]
    public void Чернетка_без_причини_не_зберігається(string reason)
    {
        var error = Assert.Throws<DomainException>(
            () => new RegistryDefinitionDraft(4, 1, "{}", reason, 9, Now));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
    }

    [Fact]
    [Trait("Directive", "BE-24")]
    public void Заміна_перебазовує_чернетку_на_нову_версію_опису()
    {
        var draft = new RegistryDefinitionDraft(4, 1, "{\"a\":1}", "перша", 9, Now);

        draft.Replace(3, "{\"a\":2}", "друга", 10, Now.AddHours(1));

        Assert.Equal(3, draft.BaseDefinitionVersion);
        Assert.Equal("{\"a\":2}", draft.ContentJson);
        Assert.Equal(10, draft.UpdatedByUserId);

        // Причина обмежена стовпцем: 1000 символів — межа, 1001 — відмова.
        draft.Replace(3, "{}", new string('x', 1000), 10, Now);
        Assert.Throws<DomainException>(() => draft.Replace(3, "{}", new string('x', 1001), 10, Now));
    }
}
