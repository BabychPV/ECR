-- COLL:an108c-session-user ── ru/kz: AN-108 / S2-05: вкладка вважає себе іншим користувачем, ніж власник cookie; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-AUTH-0409', N'ru', N'Вы вошли как другой пользователь'),
    (N'err.ECR-AUTH-0409', N'kz', N'Сіз басқа пайдаланушы ретінде кірдіңіз'),
    (N'err.ECR-AUTH-0409.sessionUserChanged', N'ru', N'В другой вкладке этого браузера вошёл другой пользователь. Изменения этой вкладки не сохранены от его имени. Перезагрузите страницу и войдите снова.'),
    (N'err.ECR-AUTH-0409.sessionUserChanged', N'kz', N'Осы браузердің басқа қойындысында басқа пайдаланушы кірді. Бұл қойындының өзгерістері оның атынан сақталмады. Бетті қайта жүктеп, қайта кіріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an108c-session-user ── кінець секції ──
