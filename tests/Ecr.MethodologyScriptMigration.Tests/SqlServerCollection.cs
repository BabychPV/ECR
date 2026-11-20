using Ecr.TestKit;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// Колекція <c>SqlServer</c>: одна реальна база на збірку (<c>ECR_TEST_SQL</c> або контейнер) — як в інших тестових
/// проєктах. Ім'я бази виводиться з каталогу збірки: <c>EcrTest_MethodologyScriptMigration_…</c>.
/// </summary>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
