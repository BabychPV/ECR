-- TC:sheet-copies-chat-ui ── ru/kz: чат таблиці на копіях аркушів - попередження у діалозі видалення копії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.sheetCopies.deleteChat', N'ru', N'Сообщений в чате копии, которые будут удалены вместе с ней: {n}.'),
    (N'documents.sheetCopies.deleteChat', N'kz', N'Көшірме чатындағы онымен бірге жойылатын хабарламалар: {n}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- TC:sheet-copies-chat-ui ── кінець секції ──

-- TC:sheet-copies-chat-ui ── чат таблиці на копіях аркушів: попередження у діалозі видалення копії; ru/kz - порцією TC:sheet-copies-chat-ui вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.sheetCopies.deleteChat', N'en', N'Chat messages of this copy that will be deleted with it: {n}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- TC:sheet-copies-chat-ui ── кінець секції ──
