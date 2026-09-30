using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Визначення колекції <c>SqlServer</c> для тестів API.
/// </summary>
/// <remarks>
/// ⚠ Колекції xUnit живуть у межах **збірки**, тому визначення з
/// <c>Ecr.Infrastructure.Tests</c> сюди не поширюється — потрібне власне
/// (`Q-053`). Бази в проєктів різні: фікстура бере ім'я з каталогу збірки
/// (<c>EcrTest_Api_&lt;мітка worktree&gt;</c>, `Q-055`), а паралельні шарди
/// <c>tools/verify-all.ps1 -ApiParallel K</c> додають суфікс <c>_s&lt;N&gt;</c>
/// з <c>ECR_TEST_SHARD</c>. <c>ECR_TEST_DB</c> перекриває ім'я цілком.
///
/// ⚠ Усередині одного процесу тести цієї колекції йдуть ПОСЛІДОВНО, з
/// однією базою. Прискорення — лише окремими процесами (див. скрипт).
/// </remarks>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
