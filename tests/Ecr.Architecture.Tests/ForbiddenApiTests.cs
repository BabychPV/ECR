using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// API, які заборонені не з міркувань смаку, а тому що кожен із них уже
/// коштував продакшену.
/// </summary>
public sealed class ForbiddenApiTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void DateTime_Now_і_UtcNow_не_використовуються_поза_реалізацією_IClock()
    {
        // ⚠ Без підмінного годинника поведінка періодів, offsets і IsLateEdit
        // невідтворювана: тест «межа періоду» пройде сьогодні і впаде першого
        // числа. Тому час читається рівно в одному місці.
        var allowed = new[]
        {
            "src/Ecr.Infrastructure/SystemClock.cs",     // сама реалізація
            "src/Ecr.DataGen",                            // генератор обсягу, не бізнес-логіка
        };

        var offenders = SourceTree.Production()
            .Where(f => !allowed.Any(a => f.Path.StartsWith(a, StringComparison.Ordinal)))
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => x.Text.Contains("DateTime.Now", StringComparison.Ordinal)
                        || x.Text.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                        || x.Text.Contains("DateTimeOffset.Now", StringComparison.Ordinal))
            .Select(x => $"{x.Path}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-11.5")]
    public void IExternalDataSink_не_існує_в_жодній_збірці()
    {
        // D-44: система ЧИТАЄ із зовнішніх джерел і не пише в них. Поява
        // «сінка» перетворила б ECR на джерело правди для PI AF, а він
        // джерело правди для вимірювань — і чиї дані правильні, стало б
        // питанням часу синхронізації.
        // ⚠ Саме по КОДУ, а не по тексту файла: слово IExternalDataSink
        // зустрічається в коментарях рівно тому, що пояснює, чому цього типу
        // немає. Перевірка по тексту ловила б власні пояснення.
        var offenders = SourceTree.Production()
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => x.Text.Contains("IExternalDataSink", StringComparison.Ordinal))
            .Select(x => $"{x.Path}:{x.Line}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void У_коді_немає_DDL_окрім_міграцій_і_генератора_вьюх()
    {
        // D-66: застосунок не має DDL-прав. DDL у коді означає або що прав
        // йому все-таки дали, або що він упаде в проді на першому виклику.
        var ddl = new Regex(@"\b(CREATE|ALTER|DROP)\s+(TABLE|INDEX|VIEW|PROCEDURE|DATABASE|SCHEMA)\b",
            RegexOptions.IgnoreCase);

        // Знову по коду: `ALTER DATABASE … WITH ROLLBACK IMMEDIATE` є в
        // коментарі до перевірки RCSI саме як пояснення, чому цю операцію
        // робить DBA, а не застосунок.
        var offenders = SourceTree.Production()
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => ddl.IsMatch(x.Text))
            .Select(x => $"{x.Path}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Публічні_асинхронні_методи_приймають_CancellationToken()
    {
        // Запит, який неможливо скасувати, тримає з'єднання і після того, як
        // клієнт пішов. У пік останнього дня періоду це прямий шлях до
        // вичерпання пулу.
        var signature = new Regex(
            @"public\s+(?:async\s+)?(?:Task|ValueTask)(?:<[^>]+>)?\s+(\w+)\s*\(([^)]*)\)",
            RegexOptions.Singleline);

        var offenders = new List<string>();
        foreach (var file in SourceTree.Production())
        {
            foreach (Match match in signature.Matches(file.Text))
            {
                var name = match.Groups[1].Value;
                var parameters = match.Groups[2].Value;

                // ⚠ Методи, чию сигнатуру диктує ЧУЖИЙ інтерфейс: додати до
                // них токен неможливо. Скасування там береться з контексту
                // (`HttpContext.RequestAborted`), а не з параметра — і це
                // видно в самому коді, не в цьому списку.
                // `Execute` — сигнатура Quartz.IJob: скасування там приходить
                // у IJobExecutionContext.CancellationToken, і саме звідти
                // QuartzJobAdapter його й бере.
                if (name is "DisposeAsync" or "InvokeAsync" or "CheckHealthAsync"
                        or "OnActionExecutionAsync" or "Execute"
                    || parameters.Contains("CancellationToken", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{file.Path}: {name}({parameters.Trim()})");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Немає_async_void_окрім_обробників_подій()
    {
        // ⚠ Виняток із `async void` неможливо спіймати: він летить у
        // SynchronizationContext і кладе процес. У вебзастосунку обробників
        // подій немає, тому винятків із правила теж немає.
        var offenders = SourceTree.Production()
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => x.Text.Contains("async void", StringComparison.Ordinal))
            .Select(x => $"{x.Path}:{x.Line}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.11")]
    public void Секрети_не_читаються_з_конфігурації_напряму_а_лише_за_іменем()
    {
        // ФВ-6.11, D-11: у appsettings.json лежать лише ІМЕНА секретів, самі
        // значення приходять зі змінних оточення. Літерал пароля в коді або
        // конфігурації — це секрет у git назавжди.
        var suspicious = new Regex(
            @"(?i)(password|pwd|secret|apikey|api_key)\s*=\s*""(?!\s*"")[^""]{3,}""");

        var offenders = SourceTree.Production()
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => suspicious.IsMatch(x.Text))
            .Select(x => $"{x.Path}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);

        // І в appsettings.json теж: там мають бути порожні значення або імена.
        var settings = File.ReadAllText(Path.Combine(SourceTree.Root, "src/Ecr.Api/appsettings.json"));
        Assert.DoesNotContain("Password=", settings, StringComparison.OrdinalIgnoreCase);
    }
}
