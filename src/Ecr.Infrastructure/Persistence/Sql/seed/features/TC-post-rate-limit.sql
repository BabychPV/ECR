-- TC:post-rate-limit ── ru/kz: S-6 (аудит 2026-11-10): межа частоти запису в чат таблиці; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyChatMessages', N'ru', N'Слишком много сообщений в чат за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyChatMessages', N'kz', N'Қысқа уақыт ішінде чатқа хабарламалар тым көп. Біраз күтіп, қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- TC:post-rate-limit ── кінець секції ──
-- TC:post-rate-limit ── S-6: межа частоти запису в чат таблиці на користувача; ru/kz — порцією TC:post-rate-limit вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyChatMessages', N'en', N'Too many chat messages in a short time. Wait a moment and try again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- TC:post-rate-limit ── кінець секції ──
