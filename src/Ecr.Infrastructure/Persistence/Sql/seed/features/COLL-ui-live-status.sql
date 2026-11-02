-- COLL:ui-live-status ── ru/kz: банер «стан аркуша змінив інший користувач» (LIVE-STATUS); власна порція; en — порція #UiEn нижче ──
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
-- COLL:ui-live-status ── en: банер «стан аркуша змінив інший користувач» (LIVE-STATUS, lane/integ-live-status-ui); ru/kz — seed/features/COLL-ui-live-status.sql ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'liveStatus.changedBy', N'en', N'Status changed by {user}: {status}', 1),
    (N'liveStatus.changedByOther', N'en', N'Status changed by another user: {status}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-live-status ── кінець секції ──
