// tests/Ecr.Api.Tests/Startup/StartupSchemaCheckTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ФВ-7.9: застосунок перевіряє сумісність схеми на старті й падає зрозуміло,
/// а не працює на невідповідній базі.
/// </summary>
/// <remarks>
/// ⛔ Перевіряється справжній старт (<c>Program.cs</c> →
/// <c>StartupSequence.RunEcrStartupSequenceAsync</c>), а не
/// <c>Infrastructure.Startup.SchemaValidator</c>: той клас production-код
/// НЕ викликає ніде (<c>git grep -n "SchemaValidator" -- src</c> — лише його
/// власне оголошення), тож тест на нього доводив би властивість, якої в
/// застосунку немає.
///
/// ⚠ Вимога реалізована ЧАСТКОВО. На старті зупиняє лише незастосована
/// міграція в режимі <c>Validate</c>. Редакція SQL і RCSI тільки логуються
/// (<c>StartupSequence.cs</c>, крок 5), запас партицій — лише попередження
/// (крок 6), наявність схем партиціонування і «база новіша за збірку» на
/// старті не перевіряються взагалі.
///
/// Мутаційний доказ: у <c>StartupSequence.ApplySchemaModeAsync</c> замінити
/// <c>throw new InvalidOperationException(…)</c> гілки Validate на
/// <c>return;</c> — застосунок стартує, і тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class StartupSchemaCheckTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.9")]
    public async Task Незастосована_міграція_зупиняє_старт_застосунку_з_її_назвою()
    {
        var (victim, productVersion) = await LastMigrationAsync();

        await ExecuteAsync(
            "DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = @id",
            ("@id", victim));
        try
        {
            var factory = new EcrApiFactory(sql);
            Exception? failure;
            try
            {
                failure = Record.Exception(() => factory.CreateClient().Dispose());
            }
            finally
            {
                // Хост, що не стартував, може кинути й на звільненні — це не
                // предмет тесту.
                _ = Record.Exception(factory.Dispose);
            }

            Assert.NotNull(failure);

            var reason = Flatten(failure).OfType<InvalidOperationException>()
                .FirstOrDefault(e => e.Message.Contains("Схема БД застаріла", StringComparison.Ordinal));

            Assert.True(reason is not null, $"Старт не зупинено перевіркою схеми; отримано: {failure}");

            // Повідомлення називає, ЩО не застосовано: «схема не збігається» без
            // переліку не дає адміністратору нічого.
            Assert.Contains(victim, reason!.Message, StringComparison.Ordinal);
            Assert.Contains("Validate", reason.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(
                "INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@id, @ver)",
                ("@id", victim), ("@ver", productVersion));
        }
    }

    private static IEnumerable<Exception> Flatten(Exception root)
    {
        var stack = new Stack<Exception>([root]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    stack.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                stack.Push(inner);
            }
        }
    }

    private async Task<(string MigrationId, string ProductVersion)> LastMigrationAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TOP (1) MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Історія міграцій порожня — фікстура не розгорнула схему.");
        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task ExecuteAsync(string text, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
