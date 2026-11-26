// tests/Ecr.Api.Tests/DataSourceCredentialContractTests.cs
using System.Reflection;
using System.Text.Json;
using Ecr.Api.Controllers;
using Ecr.Api.Startup;
using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Контракт облікових даних джерела (P-1126-SRCCRED, AC-2 частина, R-12, R-15): пароль лише входить — у тілі запиту як
/// <see cref="SecretString"/>, який не друкує значення; жодна відповідь не має властивості, що несла б пароль.
/// </summary>
/// <remarks>
/// Мутації: <c>SecretString.ToString() =&gt; value</c> — червоніють обидва тести <c>ToString</c>; <c>string? Password</c> у
/// <see cref="SaveDataSourceRequest"/> — червоніють <c>ToString</c> і тип поля; властивість <c>Password</c> у
/// <see cref="DataSourceView"/> — червоніє рефлексійний сторож.
/// </remarks>
public sealed class DataSourceCredentialContractTests
{
    /// <summary>Маркер-пароль: випадковий, щоб збіг не був випадковим.</summary>
    private static readonly string Sentinel = "ZZpwd-" + Guid.NewGuid().ToString("N");

    /// <summary>Чим закінчується ім'я типу, що виїжджає назовні у відповіді.</summary>
    private static readonly string[] OutboundSuffixes = ["View", "Response", "Result", "Dto", "Row", "Item", "Page"];

    /// <summary>Слова, які в імені властивості відповіді означають «тут лежить секрет або його похідна».</summary>
    private static readonly string[] SecretWords = ["password", "pwd", "passphrase", "protected", "secret", "credential"];

    [Fact]
    [Trait("Finding", "P-1126-SRCCRED")]
    public void Відповідь_джерела_несе_лише_ознаки_облікових_даних()
    {
        var properties = typeof(DataSourceView).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);

        Assert.Equal(typeof(SourceAuthMode), properties["AuthMode"]);
        Assert.Equal(typeof(string), properties["UserName"]);
        Assert.Equal(typeof(bool), properties["HasPassword"]);
        Assert.Equal(typeof(bool), properties["HasSecret"]);
    }

    [Fact]
    [Trait("Finding", "P-1126-SRCCRED")]
    public void Жодна_відповідь_не_має_властивості_з_паролем_чи_його_похідною()
    {
        // ⛔ R-15: пароль, блоб, довжина чи «маска» фіксованої довжини — однаково витік. Дозволена лише булева ознака
        // (hasPassword, hasSecret, mustChangePassword). Усі типи відповідей Application і Api, не лише DataSourceView: наступна
        // відповідь із джерелом усередині інакше пройшла б повз.
        var outbound = new[] { typeof(DataSourceView).Assembly, typeof(DataSourcesController).Assembly }
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => !t.IsNested && OutboundSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.Ordinal)))
            .ToList();

        Assert.Contains(typeof(DataSourceView), outbound);

        var offenders = outbound
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => SecretWords.Any(w => x.Property.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Property.PropertyType != typeof(bool))
            .Select(x => $"{x.Type.FullName}.{x.Property.Name} : {x.Property.PropertyType.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "Відповідь несе секрет: " + string.Join(", ", offenders));
    }

    [Fact]
    [Trait("Finding", "P-1126-SRCCRED")]
    public void Друк_запиту_збереження_не_розкриває_пароля()
    {
        var request = new SaveDataSourceRequest(
            "PI", new Dictionary<string, string> { ["en"] = "PI" }, ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af",
            SecretConfirmation: new SecretString(Sentinel + "-confirm"),
            AuthMode: SourceAuthMode.UserPassword,
            UserName: "reader",
            Password: new SecretString(Sentinel));

        var printed = request.ToString();

        Assert.DoesNotContain(Sentinel, printed, StringComparison.Ordinal);
        Assert.Contains("Password = ***", printed, StringComparison.Ordinal);
        Assert.Contains("UserName = reader", printed, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Finding", "P-1126-SRCCRED")]
    public void Друк_запиту_перевірки_з_форми_не_розкриває_пароля()
    {
        var request = new TestDataSourceConnectionRequest(
            7, ExternalTransport.Sql, "Server=db;Database=x", AuthMode: SourceAuthMode.UserPassword, UserName: "reader",
            Password: new SecretString(Sentinel), Reason: "check");

        Assert.DoesNotContain(Sentinel, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, $"{request.Password}", StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Finding", "P-1126-SRCCRED")]
    public void Пароль_у_запитах_джерела_лише_типу_SecretString()
    {
        // ⛔ R-12: звичайний `string` у record друкується як є. Кожна властивість запитів джерела з паролем чи секретом —
        // SecretString.
        var secretLike = new[] { typeof(SaveDataSourceRequest), typeof(TestDataSourceConnectionRequest) }
            .SelectMany(t => t.GetProperties())
            .Where(p => SecretWords.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.PropertyType != typeof(bool))
            .ToList();

        Assert.Equal(3, secretLike.Count);
        Assert.All(secretLike, p => Assert.Equal(typeof(SecretString), p.PropertyType));
    }

    [Fact]
    [Trait("Finding", "P-1126-SRCCRED")]
    public void JSON_приймає_пароль_рядком_а_назад_пише_лише_маску()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        EcrJsonSerialization.Configure(options);

        var json = $$"""
            {"code":"PI","nameL10n":{"en":"PI"},"transport":"PiSqlClient","endpoint":"Driver={PI SQL Client};AF Server=af",
             "authMode":"UserPassword","userName":"reader","password":"{{Sentinel}}","secretConfirmation":"","clearPassword":false}
            """;

        var request = JsonSerializer.Deserialize<SaveDataSourceRequest>(json, options)!;

        Assert.Equal(Sentinel, request.Password!.Reveal());
        Assert.Equal(SourceAuthMode.UserPassword, request.AuthMode);
        Assert.Null(request.SecretConfirmation);

        var back = JsonSerializer.Serialize(request, options);
        Assert.DoesNotContain(Sentinel, back, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Finding", "P-1126-SRCCRED")]
    public void Старий_клієнт_без_нових_полів_нічого_не_змінює_в_облікових_даних()
    {
        // R-08: відсутні поля = «не змінювати», а не «Windows».
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        EcrJsonSerialization.Configure(options);

        var request = JsonSerializer.Deserialize<SaveDataSourceRequest>(
            """{"code":"PI","nameL10n":{"en":"PI"},"transport":"Sql","endpoint":"Server=db"}""", options)!;

        Assert.Null(request.AuthMode);
        Assert.Null(request.UserName);
        Assert.Null(request.Password);
        Assert.False(request.ClearPassword);
    }
}
