using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Визначення колекції <c>SqlServer</c> для тестів розрахунків.
/// </summary>
/// <remarks>
/// ⚠ Колекції xUnit живуть у межах **збірки** (`Q-053`), тож визначення з
/// інших тестових проєктів сюди не поширюється. Тут воно потрібне
/// <c>ConstantReadsPerBindingTests</c> (аудит P1): число SQL-команд прогону
/// міряє лічильник EF над справжнім <c>MethodologyStore</c>, а підробка
/// сховища довела б лише, скільки разів його ПОКЛИКАЛИ, а не скільки запитів
/// пішло в базу.
/// </remarks>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
