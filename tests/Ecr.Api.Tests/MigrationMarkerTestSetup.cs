using System.Runtime.CompilerServices;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Api.Tests;

/// <summary>
/// Тести переносу версії не чекають, поки маркер «міграція в процесі» дійде до читачів (3 с у бойовому режимі):
/// там, де це важливо, тест явно опитує читача, доки той не отримає 409.
/// </summary>
internal static class MigrationMarkerTestSetup
{
#pragma warning disable CA2255 // ініціалізатор модуля навмисно: налаштування статичної затримки для всього тестового складання
    [ModuleInitializer]
    internal static void Init() => DocumentVersionMigrationStore.MarkerPropagationDelay = TimeSpan.Zero;
#pragma warning restore CA2255
}
