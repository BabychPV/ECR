using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ФВ-6.2: нижче рівня входу авторизація не знає, як користувач увійшов.
/// Рішення про доступ (профіль, редагування, періоди, область призначень)
/// не посилається ні на провайдера автентифікації, ні на типи його стека.
/// </summary>
/// <remarks>
/// ⚠ Перевірка дивиться на ТЕКСТ коду без коментарів і рядкових літералів
/// (<c>CodeLines</c>), бо заборонені посилання — це <c>using</c> і імена
/// типів, а не залежність збірки: Api легально тримає Negotiate, а Application —
/// <c>LoginHandler</c> із <c>AuthProvider</c>. Межа проходить між ВХОДОМ і
/// РІШЕННЯМ про доступ, і файли рішення названі поіменно.
///
/// ⚠ Свідомо ПОЗА переліком: <c>AccessDiagnostics.cs</c> (адміністративна
/// картка «чому в цієї людини такий доступ» показує провайдера й SID як дані
/// для діагностики, доступу вона не вирішує) і <c>LoginHandler.cs</c>.
/// Групові призначення за SID групи (<c>PrincipalSid</c>) — це дані про ролі,
/// а не знання про провайдера входу: квиток однаково дає і доменна, і
/// локальна сесія (див. <c>ICurrentUser</c>).
/// </remarks>
public sealed class AuthorizationProviderAgnosticTests
{
    /// <summary>Файли, що вирішують доступ (шлях від кореня репозиторію).</summary>
    private static readonly string[] DecisionFiles =
    [
        "src/Ecr.Application/Security/AccessProfile.cs",
        "src/Ecr.Application/Security/IAccessDecisionService.cs",
        "src/Ecr.Application/Security/DocumentReadScope.cs",
        "src/Ecr.Application/Security/EditDecision.cs",
        "src/Ecr.Application/Security/EditRules.cs",
        "src/Ecr.Application/Security/PeriodAccessRules.cs",
        "src/Ecr.Application/Security/PermissionCheck.cs",
        "src/Ecr.Application/Security/RoleAssignmentScopeRules.cs",
        "src/Ecr.Infrastructure/Security/AccessDecisionService.cs",
    ];

    /// <summary>
    /// Ознаки провайдера автентифікації: перелічення провайдера, SID
    /// Windows-облікового запису, стек Negotiate/cookie/AD.
    /// </summary>
    private static readonly Regex ProviderMarker = new(
        @"\bAuthProvider\b|\bWindowsSid\b|Microsoft\.AspNetCore\.Authentication|System\.DirectoryServices"
        + @"|System\.Security\.Principal|\bNegotiate|\bWindowsIdentity\b|\bWindowsPrincipal\b"
        + @"|\bCookieAuthentication|\bLdap",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.2")]
    public void Файли_рішення_про_доступ_не_посилаються_на_провайдера_автентифікації()
    {
        var files = SourceTree.Production("Ecr.Application", "Ecr.Infrastructure");

        // Без цього перейменування файлу мовчки виводило б його з-під сторожа.
        var missing = DecisionFiles.Where(d => files.All(f => f.Path != d)).ToList();
        Assert.Empty(missing);

        var offenders = files
            .Where(f => DecisionFiles.Contains(f.Path))
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => ProviderMarker.IsMatch(x.Text))
            .Select(x => $"{x.Path}:{x.Line}: {x.Text}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.2")]
    public void Домен_і_застосунок_не_підключають_збірок_стека_автентифікації()
    {
        var assemblies = new[]
        {
            typeof(Ecr.Domain.Abstractions.IClock).Assembly,
            typeof(Ecr.Application.Ports.ICellStore).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

            Assert.DoesNotContain(
                referenced,
                n => n.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                     && n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
            Assert.DoesNotContain(referenced, n => n.Contains("DirectoryServices", StringComparison.Ordinal));
            Assert.DoesNotContain(referenced, n => n.Contains("Negotiate", StringComparison.Ordinal));
        }
    }
}
