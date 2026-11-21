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
    (N'health.worker.quartzInProduction', N'ru', N'Production выполняет пересчёт внутри процесса приложения (Jobs:Queue:Mode = Quartz): он делит процессор с HTTP-запросами, задачи теряются при перезапуске, а цепочка месяцев, предел разветвления и исключение документов при захвате не действуют. Используйте Jobs:Queue:Mode = Database и службу EcrWorker; Quartz оставьте только для разработки и малых установок.'),
    (N'health.worker.running', N'ru', N'Пересчёт выполняется в пуле EcrWorker.'),
    (N'health.check.worker', N'kz', N'Қайта есептеу орындаушысы'),
    (N'health.worker.inProcess', N'kz', N'Қайта есептеу қолданба процесінде орындалады.'),
    (N'health.worker.serviceMissing', N'kz', N'Қайта есептеу EcrWorker қызметіне тағайындалған (Jobs:Recalculation:Executor = Worker), бірақ қызмет осы серверде орнатылмаған: қайта есептеу тапсырмалары орындаушысыз күтіп тұрады. Оны орнатыңыз (WORKER_ENABLED=1) немесе орындаушыны InProcess режиміне ауыстырыңыз.'),
    (N'health.worker.serviceDisabled', N'kz', N'Қайта есептеу EcrWorker қызметіне тағайындалған (Jobs:Recalculation:Executor = Worker), бірақ қызмет өшірілген: қайта есептеу тапсырмалары орындаушысыз күтіп тұрады. Қызметті қосыңыз немесе орындаушыны InProcess режиміне ауыстырыңыз.'),
    (N'health.worker.queueUnavailable', N'kz', N'Қайта есептеу кезегін оқу мүмкін болмады.'),
    (N'health.worker.stalled', N'kz', N'Бірде-бір қайта есептеу орындаушысы тапсырма алмайды: {minutes} мин-тан ұзақ күтіп тұрған тапсырмалар: {count}. EcrWorker қызметін тексеріңіз.'),
    (N'health.worker.quartzInProduction', N'kz', N'Production қайта есептеуді қолданба процесінің ішінде орындайды (Jobs:Queue:Mode = Quartz): ол процессорды HTTP-сұраулармен бөліседі, тапсырмалар қайта іске қосқанда жоғалады, ал айлар тізбегі, тармақтану шегі және алу кезінде құжаттарды шығару жұмыс істемейді. Jobs:Queue:Mode = Database және EcrWorker қызметін пайдаланыңыз; Quartz тек әзірлеу мен шағын орнатулар үшін қалдырыңыз.'),
    (N'health.worker.running', N'kz', N'Қайта есептеу EcrWorker пулында орындалады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:worker-health ── кінець секції ──

-- COLL:worker-health ── Перевірка `worker` на /health/ready (I2-2, `RecalculationWorkerHealthCheck`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.check.worker',                    N'en', N'Recalculation worker', 1),
    (N'health.worker.inProcess',                N'en', N'Recalculation runs in the application process.', 1),
    (N'health.worker.serviceMissing',           N'en', N'Recalculation is assigned to the EcrWorker service (Jobs:Recalculation:Executor = Worker), but the service is not installed on this server: recalculation jobs will wait with no one to run them. Install it (WORKER_ENABLED=1) or switch the executor to InProcess.', 1),
    (N'health.worker.serviceDisabled',          N'en', N'Recalculation is assigned to the EcrWorker service (Jobs:Recalculation:Executor = Worker), but the service is disabled: recalculation jobs will wait with no one to run them. Enable the service or switch the executor to InProcess.', 1),
    (N'health.worker.queueUnavailable',         N'en', N'The recalculation queue could not be read.', 1),
    (N'health.worker.stalled',                  N'en', N'No recalculation worker is taking jobs: {count} job(s) have been waiting longer than {minutes} min. Check the EcrWorker service.', 1),
    (N'health.worker.quartzInProduction',     N'en', N'Production runs recalculation inside the application process (Jobs:Queue:Mode = Quartz): it shares CPU with HTTP requests, jobs are lost on restart and the month chain, fan-out cap and claim-time document exclusion do not apply. Use Jobs:Queue:Mode = Database with the EcrWorker service; keep Quartz for development and small installations only.', 1),
    (N'health.worker.running',                  N'en', N'Recalculation runs in the EcrWorker pool.', 1)
    -- ru/kz — окремою порцією `COLL:worker-health` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:worker-health ── кінець секції ──
