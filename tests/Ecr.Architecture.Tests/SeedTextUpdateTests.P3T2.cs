using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>Тексти пакета P3 тестувального проходу №2 (<c>COLL:p3-t2</c> і суміжні правки каталогу).</summary>
public sealed partial class SeedTextUpdateTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void T2_11_Текст_забороненого_SMTP_сервера_називає_і_DNS_а_не_лише_loopback()
    {
        // ⛔ Ім'я, що не розв'язується, отримує ту саму відмову, що й loopback (fail-closed): текст має
        // дати адміністратору обидві причини, інакше він шукатиме лише loopback.
        var catalog = Catalog();

        foreach (var lang in new[] { "en", "ru", "kz" })
        {
            Assert.True(
                catalog.TryGetValue(("err.ECR-REQ-0422.smtpHostForbidden", lang), out var value),
                $"smtpHostForbidden [{lang}] немає в каталозі.");
            Assert.Contains("DNS", value, StringComparison.Ordinal);
            Assert.Contains("loopback", value, StringComparison.Ordinal);
        }
    }
}
