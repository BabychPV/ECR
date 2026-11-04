-- COLL:rc15lock ── ru/kz N-3: PATCH комірки не дочекався блокування; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-DOC-4091.lockTimeout', N'ru', N'Данные заняты долгой операцией (например, переносом другого документа на новую версию шаблона). Ничего не сохранено; повторите попытку через мгновение.'),
    (N'err.ECR-DOC-4091.lockTimeout', N'kz', N'Деректер ұзақ операциямен (мысалы, басқа құжатты үлгінің жаңа нұсқасына ауыстырумен) бос емес. Ештеңе сақталмады; сәлден кейін қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc15lock ── кінець секції ──
-- COLL:rc15lock ── N-3: PATCH комірки не дочекався блокування (LockWaitGuard); ru/kz — порцією COLL:rc15lock нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-DOC-4091.lockTimeout', N'en', N'The data is busy with a long operation (for example, moving another document to a new template version). Nothing was saved; try again in a moment.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc15lock ── кінець секції ──
