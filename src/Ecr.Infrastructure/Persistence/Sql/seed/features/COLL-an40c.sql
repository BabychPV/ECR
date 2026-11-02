-- COLL:an40c ── ru/kz AN-40 контрактного хвоста (L9-06, L9-27); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-INT-0409.eventMapConcurrency', N'ru', N'Сопоставление событий {eventMapId} изменили после того, как вы его прочитали. Перечитайте его и примените изменения заново.'),
    (N'err.ECR-REQ-0422.sourceEntityPathForeign', N'ru', N'Путь {path} не относится к каталогу подключения {dataSource} (его база AF: {database}). Откройте каталог этого подключения и выберите сущность заново.'),
    (N'err.ECR-INT-0409.eventMapConcurrency', N'kz', N'{eventMapId} оқиғалар сәйкестендіруін сіз оқығаннан кейін өзгерткен. Оны қайта оқып, өзгерістерді қайта қолданыңыз.'),
    (N'err.ECR-REQ-0422.sourceEntityPathForeign', N'kz', N'{path} жолы {dataSource} қосылымының каталогына жатпайды (оның AF базасы: {database}). Осы қосылымның каталогын ашып, нысанды қайта таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an40c ── кінець секції ──
