-- COLL:worker-health ── ru/kz перевірки worker на /health/ready (I2-2); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.check.worker', N'ru', N'Исполнитель пересчёта'),
    (N'health.worker.inProcess', N'ru', N'Пересчёт выполняется в процессе приложения.'),
    (N'health.worker.serviceMissing', N'ru', N'Пересчёт назначен службе EcrWorker (Jobs:Recalculation:Executor = Worker), но служба не установлена на этом сервере: задачи пересчёта будут ждать без исполнителя. Установите её (WORKER_ENABLED=1) или переключите исполнителя на InProcess.'),
    (N'health.worker.serviceDisabled', N'ru', N'Пересчёт назначен службе EcrWorker (Jobs:Recalculation:Executor = Worker), но служба отключена: задачи пересчёта будут ждать без исполнителя. Включите службу или переключите исполнителя на InProcess.'),
    (N'health.worker.queueUnavailable', N'ru', N'Не удалось прочитать очередь пересчёта.'),
    (N'health.worker.stalled', N'ru', N'Ни один исполнитель пересчёта не берёт задачи: задач, ожидающих дольше {minutes} мин: {count}. Проверьте службу EcrWorker.'),
    (N'health.worker.running', N'ru', N'Пересчёт выполняется в пуле EcrWorker.'),
    (N'health.check.worker', N'kz', N'Қайта есептеу орындаушысы'),
    (N'health.worker.inProcess', N'kz', N'Қайта есептеу қолданба процесінде орындалады.'),
    (N'health.worker.serviceMissing', N'kz', N'Қайта есептеу EcrWorker қызметіне тағайындалған (Jobs:Recalculation:Executor = Worker), бірақ қызмет осы серверде орнатылмаған: қайта есептеу тапсырмалары орындаушысыз күтіп тұрады. Оны орнатыңыз (WORKER_ENABLED=1) немесе орындаушыны InProcess режиміне ауыстырыңыз.'),
    (N'health.worker.serviceDisabled', N'kz', N'Қайта есептеу EcrWorker қызметіне тағайындалған (Jobs:Recalculation:Executor = Worker), бірақ қызмет өшірілген: қайта есептеу тапсырмалары орындаушысыз күтіп тұрады. Қызметті қосыңыз немесе орындаушыны InProcess режиміне ауыстырыңыз.'),
    (N'health.worker.queueUnavailable', N'kz', N'Қайта есептеу кезегін оқу мүмкін болмады.'),
    (N'health.worker.stalled', N'kz', N'Бірде-бір қайта есептеу орындаушысы тапсырма алмайды: {minutes} мин-тан ұзақ күтіп тұрған тапсырмалар: {count}. EcrWorker қызметін тексеріңіз.'),
    (N'health.worker.running', N'kz', N'Қайта есептеу EcrWorker пулында орындалады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:worker-health ── кінець секції ──

