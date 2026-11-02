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

