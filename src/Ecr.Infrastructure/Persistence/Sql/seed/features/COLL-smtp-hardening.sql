-- COLL:smtp-hardening ── ru/kz межі частоти проб транспорту; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManySmtpTests', N'ru', N'Слишком много проверочных сообщений за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManySmtpTests', N'kz', N'Қысқа уақыт ішінде тексеру хабарламалары тым көп. Біраз күтіп, қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:smtp-hardening ── кінець секції ──

-- COLL:smtp-hardening ── Межа частоти проб транспорту (рекомендація безпекового рев'ю D-263) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManySmtpTests', N'en', N'Too many test messages in a short time. Wait a moment and try again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:smtp-hardening ── кінець секції ──
