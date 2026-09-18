using Ecr.Adapters.PiAf;
using Ecr.Adapters.Sql;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Sql;

/// <summary>
/// Транспорт <c>Sql</c> має свого виконавця в контейнері (ФВ-11.2, ФВ-11.8).
/// </summary>
/// <remarks>
/// ⛔ <c>CollectionRunner</c> обирає адаптер так:
/// <c>sources.FirstOrDefault(s =&gt; s.Transport == dataSource.Transport)</c>,
/// і незнайомий транспорт завершується відмовою «не зареєстровано». Отже
/// значення перелічення без реєстрації — не половина роботи, а пастка: джерело
/// в конфігураторі оголосити можна, а зібрати з нього не можна нічим, і
/// дізнаєшся про це лише в проді, першим прогоном за розкладом.
/// </remarks>
public sealed class SqlTransportRegistrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-11.8")]
    public void Реєстрація_PI_адаптерів_НЕ_дає_виконавця_для_SQL_джерела()
    {
        // ⚠ Це не тавтологія, а фіксація межі: обидва транспорти PI — до
        // ОДНОГО й того самого PI AF (D-47). Спокуса «дописати SQL у
        // AddPiAfAdapters» повернула б рівно ту плутанину, через яку
        // `PiSqlClient` роками читався як «будь-який SQL»: насправді це
        // ODBC-драйвер PI SQL DAS (D-46), а не SQL Server.
        var services = new ServiceCollection();
        Dependencies(services);
        services.AddPiAfAdapters();

        using var provider = services.BuildServiceProvider();

        Assert.DoesNotContain(
            provider.GetServices<IExternalDataSource>(),
            s => s.Transport == ExternalTransport.Sql);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-11.8")]
    public void Реєстрація_SQL_адаптера_дає_рівно_одного_виконавця_для_SQL_джерела()
    {
        var services = new ServiceCollection();
        Dependencies(services);
        services.AddPiAfAdapters();
        services.AddSqlAdapters();

        using var provider = services.BuildServiceProvider();

        var forSql = provider.GetServices<IExternalDataSource>()
            .Where(s => s.Transport == ExternalTransport.Sql)
            .ToList();

        // ⛔ Саме ОДИН. Два виконавці одного транспорту — це дві правди про те
        // саме джерело, а `FirstOrDefault` мовчки обирає ту, що зареєстрована
        // раніше, тобто поведінка залежала б від порядку рядків у композиції.
        var adapter = Assert.Single(forSql);
        Assert.IsType<SqlDataSource>(adapter);

        // Обидва транспорти PI при цьому лишилися на місці: SQL-джерело
        // додається, а не заміщає.
        Assert.Equal(
            [ExternalTransport.PiWebApi, ExternalTransport.PiSqlClient, ExternalTransport.Sql],
            provider.GetServices<IExternalDataSource>().Select(s => s.Transport).Order());
    }

    /// <summary>Те, без чого адаптери не будуються: сховище і секрети.</summary>
    /// <param name="services">Колекція служб.</param>
    private static void Dependencies(IServiceCollection services)
    {
        services.AddSingleton(Substitute.For<ICollectionStore>());
        services.AddSingleton(Substitute.For<ISecretProvider>());
    }
}
