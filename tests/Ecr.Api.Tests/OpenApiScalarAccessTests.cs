using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Q-247: <c>/openapi/v1.json</c> і <c>/scalar</c> не мають бути анонімно
/// доступні поза розробкою.
/// </summary>
[Collection("SqlServer")]
public sealed class OpenApiScalarAccessTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поза_розробкою_openapi_і_scalar_віддають_404()
    {
        using var app = new ProductionLikeApiFactory(sql);
        using var client = app.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        var scalar = await client.GetAsync(new Uri("/scalar", UriKind.Relative));

        // ⛔ Q-247: до фіксу обидва маршрути мапилися БЕЗ жодної перевірки
        // середовища чи авторизації — 200/302 анонімно на будь-якому
        // майданчику, не лише в розробці (підтверджено реальним прогоном на
        // localhost:5084 до і після фіксу — див. запис Q-247). У Production
        // тепер обидва виклики `app.MapOpenApi()`/`app.MapScalarApiReference()`
        // не виконуються взагалі, і запит потрапляє в `MapFallback`, що вже
        // існував для `/openapi/**` і `/scalar/**` (Q-214) та віддає чіткий
        // 404 замість схеми/переглядача.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, openApi.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, scalar.StatusCode);
    }

    /// <summary>
    /// Той самий стек, що й <see cref="EcrApiFactory"/> (реальна БД,
    /// <c>Validate</c>-режим схеми), але в оточенні <c>Production</c> — саме
    /// воно визначає, чи мапляться <c>/openapi</c> і <c>/scalar</c> після
    /// Q-247. Окремий клас, а не параметр <see cref="EcrApiFactory"/>: та
    /// фабрика — спільний файл тестової інфраструктури, який може одночасно
    /// редагувати інша лінія хвилі 3, тож новий клас у власному файлі не дає
    /// перетину володіння файлом.
    /// </summary>
    private sealed class ProductionLikeApiFactory(SqlServerFixture sql) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            Environment.SetEnvironmentVariable("ECR_ConnectionStrings__Ecr", sql.ConnectionString);
            Environment.SetEnvironmentVariable("ECR_Schema__StartupMode", "Validate");
            Environment.SetEnvironmentVariable("ECR_Auth__RequireHttps", "false");
            Environment.SetEnvironmentVariable("ECR_Auth__EnableNegotiate", "false");
            Environment.SetEnvironmentVariable("ECR_Auth__StampCacheSeconds", "0");

            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging =>
            {
                // ⚠ Той самий прийом, що й у EcrApiFactory: типові
                // постачальники (зокрема EventLog на Windows) прибираються
                // цілком, інакше Dispose цієї фабрики може кинути
                // ObjectDisposedException від статичного логера Quartz, який
                // лишився від попереднього застосунку в цьому ж процесі.
                logging.ClearProviders();
            });
        }
    }
}
