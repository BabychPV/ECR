-- COLL:ui-live-status ── ru/kz: банер «стан аркуша змінив інший користувач» (LIVE-STATUS); власна порція; en — у MERGE 00-prelude.sql ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'liveStatus.changedBy', N'ru', N'Статус изменил {user}: {status}'),
    (N'liveStatus.changedBy', N'kz', N'Күйін {user} өзгертті: {status}'),
    (N'liveStatus.changedByOther', N'ru', N'Статус изменил другой пользователь: {status}'),
    (N'liveStatus.changedByOther', N'kz', N'Күйін басқа пайдаланушы өзгертті: {status}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-live-status ── кінець секції ──
