using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests;

/// <summary>
/// Визначення колекції <c>SqlServer</c> для тестів застосунку.
/// </summary>
/// <remarks>
/// ⚠ Колекції xUnit живуть у межах **збірки**, тому визначення з
/// <c>Ecr.Infrastructure.Tests</c> сюди не поширюється — потрібне власне
/// (`Q-053`). Тут воно знадобилося рівно одному класу:
/// <c>Periods/ReopenRaceTests</c>. Гонку <c>Reopen</c> і закриття періоду
/// неможливо відтворити ні підробкою, ні в пам'яті — серіалізує її саме
/// <c>UPDLOCK</c> у SQL Server, і перевіряти треба його.
/// </remarks>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
