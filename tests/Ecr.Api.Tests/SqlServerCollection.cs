using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Визначення колекції <c>SqlServer</c> для тестів API.
/// </summary>
/// <remarks>
/// ⚠ Колекції xUnit живуть у межах **збірки**, тому визначення з
/// <c>Ecr.Infrastructure.Tests</c> сюди не поширюється — потрібне власне
/// (`Q-053`). База при цьому одна: фікстура створює її під ім'ям із
/// <c>ECR_TEST_DB</c>, і два тестові проєкти по черзі працюють із тією самою.
/// </remarks>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
