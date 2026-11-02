-- COLL:an104-header-conflict ── ru/kz: AN-104 / D1-04: 409 на шапці не стирає чернетку — «ваше / чинне»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.header.conflictTitle', N'ru', N'Шапку изменил кто-то другой'),
    (N'document.header.conflictHint', N'ru', N'Пока вы редактировали, эти поля изменил другой пользователь. Ваши значения остались в форме: оставьте свои, чтобы перезаписать чужие, или возьмите текущие.'),
    (N'document.header.conflictItem', N'ru', N'{field}: ваше {yours}, текущее {value}'),
    (N'document.header.conflictKeepMine', N'ru', N'Оставить мои'),
    (N'document.header.conflictUseCurrent', N'ru', N'Взять текущие'),
    (N'document.header.conflictNoValue', N'ru', N'(нет значения)'),
    (N'document.header.conflictYes', N'ru', N'да'),
    (N'document.header.conflictNo', N'ru', N'нет'),
    (N'document.header.conflictTitle', N'kz', N'Құжаттың тақырып бөлігін басқа біреу өзгертті'),
    (N'document.header.conflictHint', N'kz', N'Сіз өңдеп жатқанда бұл өрістерді басқа пайдаланушы өзгертті. Сіздің мәндеріңіз формада қалды: басқаныкін қайта жазу үшін өзіңіздікін қалдырыңыз немесе ағымдағы мәндерді алыңыз.'),
    (N'document.header.conflictItem', N'kz', N'{field}: Сіздікі {yours}, ағымдағы {value}'),
    (N'document.header.conflictKeepMine', N'kz', N'Менікін қалдыру'),
    (N'document.header.conflictUseCurrent', N'kz', N'Ағымдағыны алу'),
    (N'document.header.conflictNoValue', N'kz', N'(мән жоқ)'),
    (N'document.header.conflictYes', N'kz', N'иә'),
    (N'document.header.conflictNo', N'kz', N'жоқ')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an104-header-conflict ── кінець секції ──

