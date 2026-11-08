-- UAT:recalc-progress ── ru/kz: прогрес перерахунку проєкту, «Locked since», видалення чернетки, вид задачі ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'recalcProgress.queuedProject', N'ru', N'Пересчёт {project} поставлен в очередь…'),
    (N'recalcProgress.queuedDocuments', N'ru', N'В очереди {total} документов на пересчёт ({project})'),
    (N'recalcProgress.running', N'ru', N'Пересчёт {project}: {done} из {total} документов, пропущено {skipped}, ошибок {failed}'),
    (N'recalcProgress.finished', N'ru', N'Завершено: пересчитано {recalculated}, пропущено {skipped} (утверждены), ошибок {failed} ({project})'),
    (N'recalcProgress.finishedWithErrors', N'ru', N'Завершено с ошибками: пересчитано {recalculated}, пропущено {skipped} (утверждены), ошибок {failed} ({project})'),
    (N'recalcProgress.failed', N'ru', N'Пересчёт {project} не выполнен'),
    (N'recalcProgress.queuedToast', N'ru', N'Пересчёт {project} поставлен в очередь. Прогресс — над списком.'),
    (N'recalcProgress.confirmScope', N'ru', N'Пересчитываются все документы {project} за {year} год. Утверждённые листы не меняются.'),
    (N'recalcProgress.confirmScopeSkipping', N'ru', N'Пересчитываются все документы {project} за {year} год. Заблокированные месяцы пропускаются: {months}. Утверждённые листы не меняются.'),
    (N'recalcProgress.confirmScopeUnknown', N'ru', N'Пересчитываются все документы {project}. Заблокированные месяцы и утверждённые листы пропускаются.'),
    (N'recalcProgress.openJobs', N'ru', N'Открыть в задачах'),
    (N'recalcProgress.openList', N'ru', N'Открыть документы'),
    (N'recalcProgress.dismiss', N'ru', N'Скрыть'),
    (N'recalcProgress.expand', N'ru', N'Показать подробности'),
    (N'recalcProgress.collapse', N'ru', N'Свернуть подробности'),
    (N'recalcProgress.progressLabel', N'ru', N'Прогресс пересчёта {project}'),
    (N'recalcProgress.showFailed', N'ru', N'Документы с ошибкой ({failed})'),
    (N'recalcProgress.failedListError', N'ru', N'Не удалось загрузить документы с ошибкой.'),
    (N'recalcProgress.myTaskTitle', N'ru', N'Пересчёт проекта {project}'),
    (N'jobs.filterProject', N'ru', N'Проект'),
    (N'jobs.partOfProjectRecalc', N'ru', N'часть пересчёта проекта'),
    (N'periods.tile.lockedSince', N'ru', N'Заблокирован с {date}'),
    (N'err.ECR-REQ-0422.jobKind', N'ru', N'Вида задачи «{kind}» не существует.'),
    (N'err.ECR-PRD-0409.deleteNotDraft', N'ru', N'Удалить можно только черновик проекта без документов (этот: {status}, документов {documentCount}).'),
    (N'err.ECR-PRD-0409.deleteHasDocuments', N'ru', N'Удалить можно только черновик проекта без документов (в этом документов {documentCount}).'),
    (N'err.ECR-PRD-0409.deleteHasHistory', N'ru', N'У проекта уже есть история расчётов, срезов или архивации ({historyCount}): удалить нельзя.'),
    (N'recalcProgress.queuedProject', N'kz', N'{project} қайта есептеу кезекке қойылды…'),
    (N'recalcProgress.queuedDocuments', N'kz', N'Қайта есептеуге {total} құжат кезекте ({project})'),
    (N'recalcProgress.running', N'kz', N'{project} қайта есептелуде: {total} құжаттың {done}, өткізілді {skipped}, қате {failed}'),
    (N'recalcProgress.finished', N'kz', N'Аяқталды: қайта есептелді {recalculated}, өткізілді {skipped} (бекітілген), қате {failed} ({project})'),
    (N'recalcProgress.finishedWithErrors', N'kz', N'Қателермен аяқталды: қайта есептелді {recalculated}, өткізілді {skipped} (бекітілген), қате {failed} ({project})'),
    (N'recalcProgress.failed', N'kz', N'{project} қайта есептеу орындалмады'),
    (N'recalcProgress.queuedToast', N'kz', N'{project} қайта есептеу кезекке қойылды. Барысы — тізім үстінде.'),
    (N'recalcProgress.confirmScope', N'kz', N'{project} жобасының {year} жылғы барлық құжаттары қайта есептеледі. Бекітілген парақтар өзгермейді.'),
    (N'recalcProgress.confirmScopeSkipping', N'kz', N'{project} жобасының {year} жылғы барлық құжаттары қайта есептеледі. Бұғатталған айлар өткізіледі: {months}. Бекітілген парақтар өзгермейді.'),
    (N'recalcProgress.confirmScopeUnknown', N'kz', N'{project} жобасының барлық құжаттары қайта есептеледі. Бұғатталған айлар мен бекітілген парақтар өткізіледі.'),
    (N'recalcProgress.openJobs', N'kz', N'Тапсырмаларда ашу'),
    (N'recalcProgress.openList', N'kz', N'Құжаттарды ашу'),
    (N'recalcProgress.dismiss', N'kz', N'Жасыру'),
    (N'recalcProgress.expand', N'kz', N'Толығырақ көрсету'),
    (N'recalcProgress.collapse', N'kz', N'Толығырақты жию'),
    (N'recalcProgress.progressLabel', N'kz', N'{project} қайта есептеу барысы'),
    (N'recalcProgress.showFailed', N'kz', N'Қатесі бар құжаттар ({failed})'),
    (N'recalcProgress.failedListError', N'kz', N'Қатесі бар құжаттарды жүктеу мүмкін болмады.'),
    (N'recalcProgress.myTaskTitle', N'kz', N'{project} жобасын қайта есептеу'),
    (N'jobs.filterProject', N'kz', N'Жоба'),
    (N'jobs.partOfProjectRecalc', N'kz', N'жобаны қайта есептеудің бөлігі'),
    (N'periods.tile.lockedSince', N'kz', N'{date} бастап бұғатталған'),
    (N'err.ECR-REQ-0422.jobKind', N'kz', N'«{kind}» тапсырма түрі жоқ.'),
    (N'err.ECR-PRD-0409.deleteNotDraft', N'kz', N'Тек құжатсыз жоба жобасын жоюға болады (бұл: {status}, құжаттар {documentCount}).'),
    (N'err.ECR-PRD-0409.deleteHasDocuments', N'kz', N'Тек құжатсыз жоба жобасын жоюға болады (мұнда құжаттар {documentCount}).'),
    (N'err.ECR-PRD-0409.deleteHasHistory', N'kz', N'Жобада есептеу, кесінді немесе мұрағаттау тарихы бар ({historyCount}): жою мүмкін емес.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UAT:recalc-progress ── кінець секції ──
-- UAT:recalc-progress ── прогрес перерахунку проєкту, «Locked since», видалення чернетки, вид задачі (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'recalcProgress.queuedProject', N'en', N'Queued recalculation of {project}…', 1),
    (N'recalcProgress.queuedDocuments', N'en', N'Queued {total} documents for recalculation ({project})', 1),
    (N'recalcProgress.running', N'en', N'Recalculating {project}: {done} of {total} documents, {skipped} skipped, {failed} failed', 1),
    (N'recalcProgress.finished', N'en', N'Finished: {recalculated} recalculated, {skipped} skipped (approved), {failed} failed ({project})', 1),
    (N'recalcProgress.finishedWithErrors', N'en', N'Finished with errors: {recalculated} recalculated, {skipped} skipped (approved), {failed} failed ({project})', 1),
    (N'recalcProgress.failed', N'en', N'Recalculation of {project} failed', 1),
    (N'recalcProgress.queuedToast', N'en', N'Recalculation of {project} queued. Progress is shown above the list.', 1),
    (N'recalcProgress.confirmScope', N'en', N'Recalculates all documents of {project} for {year}. Approved sheets are not changed.', 1),
    (N'recalcProgress.confirmScopeSkipping', N'en', N'Recalculates all documents of {project} for {year}. Locked months are skipped: {months}. Approved sheets are not changed.', 1),
    (N'recalcProgress.confirmScopeUnknown', N'en', N'Recalculates all documents of {project}. Locked months and approved sheets are skipped.', 1),
    (N'recalcProgress.openJobs', N'en', N'Open in Jobs', 1),
    (N'recalcProgress.openList', N'en', N'Open documents', 1),
    (N'recalcProgress.dismiss', N'en', N'Dismiss', 1),
    (N'recalcProgress.expand', N'en', N'Show details', 1),
    (N'recalcProgress.collapse', N'en', N'Hide details', 1),
    (N'recalcProgress.progressLabel', N'en', N'Recalculation progress of {project}', 1),
    (N'recalcProgress.showFailed', N'en', N'Failed documents ({failed})', 1),
    (N'recalcProgress.failedListError', N'en', N'Could not load the failed documents.', 1),
    (N'recalcProgress.myTaskTitle', N'en', N'Project recalculation {project}', 1),
    (N'jobs.filterProject', N'en', N'Project', 1),
    (N'jobs.partOfProjectRecalc', N'en', N'part of a project recalculation', 1),
    (N'periods.tile.lockedSince', N'en', N'Locked since {date}', 1),
    (N'err.ECR-REQ-0422.jobKind', N'en', N'Job kind «{kind}» does not exist.', 1),
    (N'err.ECR-PRD-0409.deleteNotDraft', N'en', N'Only draft projects without documents can be deleted (this one: {status}, {documentCount} documents).', 1),
    (N'err.ECR-PRD-0409.deleteHasDocuments', N'en', N'Only draft projects without documents can be deleted (this one has {documentCount} documents).', 1),
    (N'err.ECR-PRD-0409.deleteHasHistory', N'en', N'The project already has calculation, snapshot or archive history ({historyCount} records) and cannot be deleted.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UAT:recalc-progress ── кінець секції ──
