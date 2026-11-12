-- NOTIF:clear-webhook-confirm ── ru/kz підтвердження «Remove URL» вебхука (аудит 2026-11 U-3); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'notifications.clearWebhookTitle', N'ru', N'Удалить URL веб-хука канала «{name}»?'),
    (N'notifications.clearWebhookTitle', N'kz', N'«{name}» арнасының веб-хук URL мекенжайын жою керек пе?'),
    (N'notifications.clearWebhookText', N'ru', N'Адрес нельзя будет посмотреть снова — его придётся заново скопировать из Teams. Пока адреса нет, уведомления в этот канал не доставляются.'),
    (N'notifications.clearWebhookText', N'kz', N'Мекенжайды қайта көру мүмкін болмайды — оны Teams-тен қайта көшіру керек. Мекенжай жоқ кезде бұл арнаға хабарландырулар жеткізілмейді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NOTIF:clear-webhook-confirm ── кінець секції ──
-- NOTIF:clear-webhook-confirm ── en (аудит 2026-11 U-3); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notifications.clearWebhookTitle', N'en', N'Remove the webhook URL of channel "{name}"?', 1),
    (N'notifications.clearWebhookText', N'en', N'The address cannot be viewed again — you will have to copy it from Teams anew. Until then notifications to this channel are not delivered.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NOTIF:clear-webhook-confirm ── кінець секції ──
