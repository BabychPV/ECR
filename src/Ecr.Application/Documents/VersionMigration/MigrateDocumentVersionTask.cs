using Ecr.Application.Common;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Завдання фонового переносу проєкту документа на нову версію шаблону (D-2 RC15B).</summary>
/// <param name="DocumentId">Документ, з якого відкрили перенос (його проєкт переноситься весь).</param>
/// <param name="TargetVersionId">Цільова версія того самого шаблону.</param>
/// <param name="Mode">Режим (<see cref="VersionMigrationMode"/> рядком: payload не залежить від налаштувань конвертерів).</param>
/// <param name="Actor">
/// Хто поставив задачу — від його імені вона й виконується (права, гранти, підпис журналу), як
/// <see cref="ExcelImportTask"/> (F-01). <c>null</c> — завдання без автора: відмовляє <c>ECR-AUTH-0401</c>.
/// </param>
public sealed record MigrateDocumentVersionTask(long DocumentId, int TargetVersionId, string Mode, JobActor? Actor = null);

/// <summary>Стадії переносу, які сховище називає приймачу прогресу.</summary>
public static class VersionMigrationStages
{
    /// <summary>Значення комірок і рядки таблиць (переклад колонок, видалення зниклих).</summary>
    public const string Cells = "cells";

    /// <summary>Екземпляри таблиць.</summary>
    public const string Instances = "instances";

    /// <summary>Нові рядки, яких у старій версії не було.</summary>
    public const string NewRows = "newRows";

    /// <summary>Шапка документів.</summary>
    public const string Header = "header";

    /// <summary>Індекс пошуку.</summary>
    public const string Index = "index";

    /// <summary>Склад документа й робочий процес.</summary>
    public const string Workflow = "workflow";

    /// <summary>Результати валідації.</summary>
    public const string Validation = "validation";

    /// <summary>Перемикання версії проєкту.</summary>
    public const string Finish = "finish";
}

/// <summary>
/// Приймач прогресу всередині сховища переносу: після кожної команди (пачки).
/// </summary>
/// <param name="stage">Стадія (<see cref="VersionMigrationStages"/>).</param>
/// <param name="stepsDone">Скільки кроків завершено ДО поточної команди.</param>
/// <param name="steps">Кроків усього.</param>
/// <param name="changedRows">Скільки рядків змінено в поточному кроці досі (пачкові кроки); 0 — для одиничних.</param>
/// <param name="ct">Токен скасування.</param>
public delegate Task VersionMigrationStepReporter(
    string stage, int stepsDone, int steps, long changedRows, CancellationToken ct);

/// <summary>
/// Приймач прогресу фонового переносу: відсоток і структурований ключ повідомлення (<c>jobs.migrate*</c>).
/// </summary>
/// <param name="percent">Відсоток 0–100.</param>
/// <param name="messageKey">Ключ каталогу.</param>
/// <param name="parameters">Параметри підстановки; <c>null</c> — без них.</param>
/// <param name="ct">Токен скасування.</param>
public delegate Task VersionMigrationProgressReporter(
    int percent, string messageKey, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct);
