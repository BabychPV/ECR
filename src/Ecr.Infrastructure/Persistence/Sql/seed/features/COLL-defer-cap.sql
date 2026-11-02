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

