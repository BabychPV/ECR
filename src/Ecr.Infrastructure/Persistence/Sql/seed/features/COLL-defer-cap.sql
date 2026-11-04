-- COLL:defer-cap ── ru/kz стелі відкладень задачі (борг O1); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.deferralExhausted', N'ru', N'Остановлено: задача ждала освобождения ресурса {resource} {waited} — дольше предела откладывания. Проверьте, что удерживает ресурс, и перезапустите задачу.'),
    (N'jobs.deferralExhausted', N'kz', N'Тоқтатылды: тапсырма {resource} ресурсының босауын {waited} күтті — кейінге қалдыру шегінен ұзақ. Ресурсты не ұстап тұрғанын тексеріп, тапсырманы қайта іске қосыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:defer-cap ── кінець секції ──

-- COLL:defer-cap ── Стеля відкладень задачі, що чекає зайнятий ресурс (борг O1, `JobDeferral`, `JobWorker`, `QuartzJobAdapter`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- `{resource}` — ім'я лока (`ecr:recalc:doc:{id}`), `{waited}` — hh:mm:ss від першого відкладення. Failed без ретраю.
    (N'jobs.deferralExhausted',                 N'en', N'Stopped: the job waited {waited} for resource {resource} to be released, longer than the deferral limit. Check what holds the resource, then restart the job.', 1)
    -- ru/kz — окремою порцією `COLL:defer-cap` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:defer-cap ── кінець секції ──
