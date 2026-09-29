// src/Ecr.Worker/Isolation/ChildCommand.cs

namespace Ecr.Worker.Isolation;

/// <summary>Команда запуску дочірнього воркера.</summary>
/// <param name="FileName">Виконуваний файл.</param>
/// <param name="Arguments">Аргументи (разом із <c>--child</c>).</param>
public sealed record ChildCommand(string FileName, IReadOnlyList<string> Arguments);
