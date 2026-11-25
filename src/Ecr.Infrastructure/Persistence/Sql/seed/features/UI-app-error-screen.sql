-- COLL:ui-app-error-screen ── en: екран помилки застосунку (RenderErrorScreen) — ✎ 25.11, людина: «виправь це» (розробницький екран React Router після оновлення); ПУБЛІЧНА область (0): екран буває й до входу ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'appError.renderTitle',      N'en', N'This screen could not be displayed', 0),
    (N'appError.renderDetail',     N'en', N'The application hit an internal error while drawing this screen. Navigation still works: you can reload the page or go back to the document list.', 0),
    (N'appError.staleTitle',       N'en', N'A new version is installed', 0),
    (N'appError.staleDetail',      N'en', N'This tab is running an older build of the application and can no longer load part of itself. Reload the page to get the new version.', 0),
    (N'appError.reloading',        N'en', N'Loading the new version...', 0),
    (N'appError.technicalDetails', N'en', N'Technical details', 0),
    (N'appError.reload',           N'en', N'Reload page', 0),
    (N'appError.toDocuments',      N'en', N'Go to document list', 0)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-app-error-screen ── кінець секції ──

-- COLL:ui-app-error-screen ── ru/kz: той самий екран; en — порцією вище ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'appError.renderTitle',      N'ru', N'Не удалось показать этот экран'),
    (N'appError.renderTitle',      N'kz', N'Бұл экранды көрсету мүмкін болмады'),
    (N'appError.renderDetail',     N'ru', N'При отрисовке экрана в приложении произошла внутренняя ошибка. Навигация работает: можно перезагрузить страницу или вернуться к списку документов.'),
    (N'appError.renderDetail',     N'kz', N'Экранды салу кезінде қолданбада ішкі қате болды. Навигация жұмыс істейді: бетті қайта жүктеуге немесе құжаттар тізіміне оралуға болады.'),
    (N'appError.staleTitle',       N'ru', N'Установлена новая версия'),
    (N'appError.staleTitle',       N'kz', N'Жаңа нұсқа орнатылды'),
    (N'appError.staleDetail',      N'ru', N'В этой вкладке открыта старая сборка приложения, и она больше не может загрузить свою часть. Перезагрузите страницу, чтобы получить новую версию.'),
    (N'appError.staleDetail',      N'kz', N'Бұл қойындыда қолданбаның ескі нұсқасы ашық және ол өз бөлігін енді жүктей алмайды. Жаңа нұсқаны алу үшін бетті қайта жүктеңіз.'),
    (N'appError.reloading',        N'ru', N'Загрузка новой версии...'),
    (N'appError.reloading',        N'kz', N'Жаңа нұсқа жүктелуде...'),
    (N'appError.technicalDetails', N'ru', N'Технические подробности'),
    (N'appError.technicalDetails', N'kz', N'Техникалық мәліметтер'),
    (N'appError.reload',           N'ru', N'Перезагрузить страницу'),
    (N'appError.reload',           N'kz', N'Бетті қайта жүктеу'),
    (N'appError.toDocuments',      N'ru', N'К списку документов'),
    (N'appError.toDocuments',      N'kz', N'Құжаттар тізіміне')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-app-error-screen ── кінець секції ──
