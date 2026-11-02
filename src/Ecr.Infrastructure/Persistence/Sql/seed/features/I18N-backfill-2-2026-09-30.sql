-- I18N:backfill-2 2026-09-30 ── ru/kz для ключів, що мали лише en (правило перевірки, причина публікації звіту); переклади машинні; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.validationScope', N'ru', N'Область правила должна быть 0 (ячейка), 1 (строка), 2 (таблица) или 3 (документ): с любым другим значением сохранилось бы правило, которое никогда не выполняется.'),
    (N'err.ECR-REQ-0422.validationScope', N'kz', N'Ереже аясы 0 (ұяшық), 1 (жол), 2 (кесте) немесе 3 (құжат) болуы керек: басқа кез келген мәнмен ешқашан орындалмайтын ереже сақталар еді.'),
    (N'err.ECR-REQ-0422.reportPublishReason', N'ru', N'Укажите причину публикации: пустая строка ничего не объяснит тому, кто позже спросит, почему эта версия отчёта введена в действие.'),
    (N'err.ECR-REQ-0422.reportPublishReason', N'kz', N'Жариялау себебін көрсетіңіз: бос жол кейін есептің осы нұсқасы неліктен қолданысқа енгізілгенін сұрайтын адамға ештеңе түсіндірмейді.'),
    (N'reportDefs.publishReasonTitle', N'ru', N'Опубликовать версию отчёта {version}'),
    (N'reportDefs.publishReasonTitle', N'kz', N'Есептің {version} нұсқасын жариялау'),
    (N'reportDefs.publishReason', N'ru', N'Причина публикации'),
    (N'reportDefs.publishReason', N'kz', N'Жариялау себебі')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- I18N:backfill-2 ── кінець секції ──

