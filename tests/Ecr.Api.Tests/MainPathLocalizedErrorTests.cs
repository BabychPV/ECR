using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відмови головних шляхів (вхід і пароль, робочий процес аркуша, періоди,
/// 500) доїжджають клієнтові реченням із каталогу — без кирилиці й без
/// нерозкритого <c>{плейсхолдера}</c>.
/// </summary>
/// <remarks>
/// ⛔ Каталог тут — САМ <c>09-seed.sql</c>, а кидок — СПРАВЖНІЙ код, не
/// відтворення поруч: інакше тест лишився б зеленим, коли шаблон і кидок
/// розійшлися б в імені підстановки або в її ТИПІ. Резолвер підставляє лише
/// <c>string</c> — число в <c>Details</c> тихо лишає <c>{minLength}</c> у
/// реченні, і саме це тут червоніє.
/// </remarks>
public sealed partial class MainPathLocalizedErrorTests
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Закороткий_новий_пароль_називає_межу_числом_а_не_плейсхолдером()
    {
        var user = new User("operator", "Operator", AuthProvider.Local);
        user.SetPassword("hash");

        var users = Substitute.For<IUserStore>();
        users.FindByIdAsync(9, Arg.Any<CancellationToken>()).Returns(user);
        users.GetPolicyAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
             .Returns(new PasswordPolicy("Default", minLength: 12, maxFailedAttempts: 5));

        var hasher = Substitute.For<IPasswordHasher>();
        hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(9);

        var handler = new ChangePasswordHandler(
            users, hasher, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(),
            currentUser, Substitute.For<IClock>());

        var detail = await DetailAsync(() => handler.HandleAsync("old", "short", CancellationToken.None));

        Assert.Equal("The new password is shorter than 12 characters.", detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Анонімний_запит_і_разовий_пароль_доїжджають_з_каталогу()
    {
        var anonymous = Substitute.For<ICurrentUser>();
        anonymous.UserId.Returns((int?)null);

        var signIn = await DetailAsync(() => PermissionCheck.RequireAsync(
            Substitute.For<IAccessDecisionService>(), anonymous, "Document.View", CancellationToken.None));
        var gate = await DetailAsync(() =>
        {
            PasswordChangeGate.Ensure(true, "/api/v1/documents/7");
            return Task.CompletedTask;
        });

        Assert.StartsWith("You are not signed in", signIn, StringComparison.Ordinal);
        Assert.StartsWith("Your password was issued for one-time use", gate, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Затвердження_чернетки_називає_стан_аркуша()
    {
        var state = new ApprovalState(documentId: 700, sheetDefId: 3, periodKey: 202601);

        var detail = await DetailAsync(() =>
        {
            state.Approve(9, Now);
            return Task.CompletedTask;
        });

        Assert.Equal("Only a submitted sheet can be approved; the sheet is Draft.", detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відкриття_незакритого_періоду_називає_його_стан()
    {
        var period = new Period(1, new PeriodKey(202601), 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var detail = await DetailAsync(() =>
        {
            period.Reopen(Now.AddDays(1), "late data", Now);
            return Task.CompletedTask;
        });

        Assert.Equal($"Only a closed period can be reopened; the period is {period.State}.", detail);
    }

    /// <summary>
    /// `U-01`: хибний пароль пояснює себе реченням, а не самим кодом.
    /// </summary>
    /// <remarks>
    /// ⛔ Предмет — не «механізм `messageKey` працює» (це доводить
    /// <c>GenericMessageKeyLocalizationTests</c>), а те, що ключ несе САМ
    /// <see cref="LoginHandler"/>. Без ключа <c>ErrorAlert.tsx</c> (рішення
    /// 2026-09-20) не друкує подробицю ВЗАГАЛІ, і користувач бачить самі лише
    /// «Sign in to continue.» плюс код: екран не каже, що сталося.
    ///
    /// ⚠ Прогін іде крізь справжній конвеєр і справжній <c>09-seed.sql</c>,
    /// тому тест червоніє і від знятого ключа в коді, і від прибраного рядка
    /// каталогу, і від кирилиці, що просочилася назад у <c>detail</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Хибний_пароль_називає_причину_а_не_лише_код()
    {
        var user = new User("ux-admin", "UX Admin", AuthProvider.Local);
        user.SetPassword("hash-of-the-real-password");

        var users = Substitute.For<IUserStore>();
        users.FindByUserNameAsync("ux-admin", Arg.Any<CancellationToken>()).Returns(user);
        users.GetPolicyAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
             .Returns(new PasswordPolicy("Default", minLength: 12, maxFailedAttempts: 5));

        var hasher = Substitute.For<IPasswordHasher>();
        hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var handler = new LoginHandler(
            users, hasher, Substitute.For<IUnitOfWork>(), clock,
            NullLogger<LoginHandler>.Instance);

        var detail = await DetailAsync(
            () => handler.HandleAsync("ux-admin", "wrong-password", "127.0.0.1", CancellationToken.None));

        Assert.Equal("The user name or password is incorrect.", detail);
    }

    /// <summary>
    /// `U-02`: відмова типу комірки називає колонку й очікуваний тип — мовою
    /// інтерфейсу.
    /// </summary>
    /// <remarks>
    /// ⛔ Це найчастіша інтерактивна відмова продукту: її бачить кожен, хто
    /// набрав не той тип у комірку. Речення збиралося рядком у
    /// <c>CellValueReader.Mismatch</c>, тож на англійському екрані під
    /// англійським заголовком стояло «Колонка «C5» очікує число.».
    ///
    /// ⚠ Перевіряються ОБИДВА боки правки: і що ключ резолвиться (інакше
    /// повернулося б запасне українське речення — його ловить перевірка на
    /// кирилицю в <see cref="DetailAsync"/>), і що <c>{columnCode}</c> справді
    /// підставлено (інакше <c>detail</c> ніс би фігурні дужки — це теж
    /// перевіряє <see cref="DetailAsync"/>), і що тип узято ТОЙ САМИЙ: числова
    /// колонка не має пояснювати себе датою.
    /// </remarks>
    [Theory]
    [InlineData(CellDataType.Decimal, "abc", "Column \"C5\" expects a number.")]
    [InlineData(CellDataType.Bool, "abc", "Column \"C5\" expects true or false.")]
    [InlineData(CellDataType.Date, "abc", "Column \"C5\" expects a date.")]
    [InlineData(CellDataType.Lookup, "abc", "Column \"C5\" expects the identifier of a registry entry.")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відмова_типу_комірки_називає_колонку_мовою_інтерфейсу(
        CellDataType dataType, string typed, string expected)
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create("C5"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "C5" }), 1, dataType);

        var detail = await DetailAsync(() =>
        {
            CellValueReader.Read(typed, column);
            return Task.CompletedTask;
        });

        Assert.Equal(expected, detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Необроблений_виняток_віддає_каталожне_речення_без_тексту_винятку()
    {
        var detail = await DetailAsync(
            () => throw new InvalidOperationException("Invalid object name 'doc.Secret'."));

        Assert.Equal("Internal error. Contact your administrator and quote the correlation ID.", detail);
    }

    /// <summary>Проганяє відмову крізь конвеєр і повертає перевірену <c>detail</c>.</summary>
    private static async Task<string> DetailAsync(Func<Task> act)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Language.Returns("en");

        var services = new ServiceCollection();
        services.AddSingleton<IUiStringCatalog>(SeedCatalog());
        services.AddSingleton(currentUser);

        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => act(), NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        var detail = JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString();

        Assert.NotNull(detail);
        Assert.DoesNotMatch("[а-яА-ЯіІїЇєЄ]", detail);

        // ⛔ Нерозкритий плейсхолдер: підстановки немає в Details або вона не рядок.
        Assert.DoesNotMatch(@"\{[A-Za-z_]\w*\}", detail);

        return detail;
    }

    /// <summary>Рядки <c>err.*</c> мовою збірки — прямо з <c>09-seed.sql</c>.</summary>
    private static FakeUiStringCatalog SeedCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var seed = File.ReadAllText(Path.Combine(
            dir.FullName, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        var catalog = new FakeUiStringCatalog();
        foreach (Match row in SeedRow().Matches(seed))
        {
            catalog.Add(
                "en", row.Groups[1].Value, row.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal),
                UiStringScope.Private);
        }

        return catalog;
    }

    [GeneratedRegex(@"\(\s*N'(err\.[^']+)'\s*,\s*N'en'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedRow();
}
