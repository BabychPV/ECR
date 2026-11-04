-- COLL:pm-workflow-denial-reasons ── ru/kz: PM-PASS-A-P3 п.7: причини відмови Подання/Повернення в роботу; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'deny.reopen.NoRight', N'ru', N'нет права вернуть этот лист в работу: нужен доступ на утверждение, а при маршруте согласования — роль его шага'),
    (N'deny.reopen.NoRight', N'kz', N'бұл парақты жұмысқа қайтаруға құқығыңыз жоқ: бекіту деңгейіндегі қолжетімділік қажет, ал келісу маршрутында — оның қадамының рөлі'),
    (N'deny.reopen.BusinessRule', N'ru', N'лист не подан и не утверждён, возвращать в работу нечего'),
    (N'deny.reopen.BusinessRule', N'kz', N'парақ тапсырылмаған да, бекітілмеген де, жұмысқа қайтаратын ештеңе жоқ'),
    (N'deny.submit.NoRight', N'ru', N'нет права подать этот лист: в этом проекте нужен доступ на подачу'),
    (N'deny.submit.NoRight', N'kz', N'бұл парақты тапсыруға құқығыңыз жоқ: осы жобада тапсыру қолжетімділігі қажет'),
    (N'deny.reason.Unknown', N'ru', N'действие сейчас недоступно'),
    (N'deny.reason.Unknown', N'kz', N'әрекет қазір қолжетімді емес')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:pm-workflow-denial-reasons ── кінець секції ──

-- COLL:pm-workflow-denial-reasons ── PM-PASS-A-P3 п.7: en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'deny.reopen.NoRight', N'en', N'you do not have the right to return this sheet to work: the approval access level is required, and with an approval route also the role of its step', 1),
    (N'deny.reopen.BusinessRule', N'en', N'the sheet is neither submitted nor approved, so there is nothing to return to work', 1),
    (N'deny.submit.NoRight', N'en', N'you do not have the right to submit this sheet: submit access is required in this project', 1),
    (N'deny.reason.Unknown', N'en', N'the action is not allowed right now', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:pm-workflow-denial-reasons ── кінець секції ──
