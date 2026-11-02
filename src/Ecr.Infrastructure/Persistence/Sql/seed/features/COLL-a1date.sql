-- COLL:a1date ── ru/kz A1-02: строгий розбір набраної дати; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'dates.invalid', N'ru', N'«{value}» — некорректная дата. Введите дату в формате ДД.ММ.ГГГГ или ГГГГ-ММ-ДД.'),
    (N'dates.outOfRange', N'ru', N'Дата {value} вне допустимого диапазона.'),
    (N'dates.invalid', N'kz', N'«{value}» — жарамсыз күн. Күнді КК.АА.ЖЖЖЖ немесе ЖЖЖЖ-АА-КК пішімінде енгізіңіз.'),
    (N'dates.outOfRange', N'kz', N'{value} күні рұқсат етілген ауқымнан тыс.'),
    (N'dates.incomplete', N'ru', N'Дата введена не полностью или не существует. Допишите её или нажмите Esc для отмены.'),
    (N'dates.incomplete', N'kz', N'Күн толық енгізілмеген немесе жоқ. Оны толықтырыңыз немесе бас тарту үшін Esc басыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a1date ── кінець секції ──
