-- COLL:ui40board ── ru/kz UI-40: подання Board переліку документів (перемикач Table/Board, стовпці за станом); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.view', N'ru', N'Вид'),
    (N'documents.view', N'kz', N'Көрініс'),
    (N'documents.viewTable', N'ru', N'Таблица'),
    (N'documents.viewTable', N'kz', N'Кесте'),
    (N'documents.viewBoard', N'ru', N'Доска'),
    (N'documents.viewBoard', N'kz', N'Тақта'),
    (N'documents.board.draft', N'ru', N'Черновик'),
    (N'documents.board.draft', N'kz', N'Жоба'),
    (N'documents.board.waiting', N'ru', N'Ожидает утверждения'),
    (N'documents.board.waiting', N'kz', N'Бекітуді күтуде'),
    (N'documents.board.rework', N'ru', N'Возвращено или отклонено'),
    (N'documents.board.rework', N'kz', N'Қайтарылған немесе қабылданбаған'),
    (N'documents.board.approved', N'ru', N'Утверждено'),
    (N'documents.board.approved', N'kz', N'Бекітілген'),
    (N'documents.board.noState', N'ru', N'Нет состояния за этот период'),
    (N'documents.board.noState', N'kz', N'Осы кезеңге күйі жоқ'),
    (N'documents.board.nothingHere', N'ru', N'Здесь пусто'),
    (N'documents.board.nothingHere', N'kz', N'Мұнда ештеңе жоқ')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui40board ── кінець секції ──
-- COLL:ui40board ── UI-40: подання Board переліку документів (перемикач Table/Board, стовпці за станом); ru/kz — порцією COLL:ui40board у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.view', N'en', N'View', 1),
    (N'documents.viewTable', N'en', N'Table', 1),
    (N'documents.viewBoard', N'en', N'Board', 1),
    (N'documents.board.draft', N'en', N'Draft', 1),
    (N'documents.board.waiting', N'en', N'Waiting for approval', 1),
    (N'documents.board.rework', N'en', N'Returned or rejected', 1),
    (N'documents.board.approved', N'en', N'Approved', 1),
    (N'documents.board.noState', N'en', N'No state for this period', 1),
    (N'documents.board.nothingHere', N'en', N'Nothing here', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui40board ── кінець секції ──
