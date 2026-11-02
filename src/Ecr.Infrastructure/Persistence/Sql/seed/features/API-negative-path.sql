-- API:negative-path ── ru/kz відмов замість 500 на некоректних полях запиту; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.versionNumber', N'ru', N'Номер версии методики должен содержать от 1 до {maxLength} символов.'),
    (N'err.ECR-CALC-0422.versionNumber', N'kz', N'Әдістеме нұсқасының нөмірі 1-ден {maxLength} таңбаға дейін болуы тиіс.'),
    (N'err.ECR-CALC-0422.formulaTooLong', N'ru', N'Выражение формулы «{code}» длиннее {maxLength} символов.'),
    (N'err.ECR-CALC-0422.formulaTooLong', N'kz', N'«{code}» формуласының өрнегі {maxLength} таңбадан ұзын.'),
    (N'err.ECR-CALC-0422.testToleranceNegative', N'ru', N'Допуск теста «{code}» не может быть отрицательным: такой тест не пройдёт никогда.'),
    (N'err.ECR-CALC-0422.testToleranceNegative', N'kz', N'«{code}» тестінің рұқсат шегі теріс бола алмайды: мұндай тест ешқашан өтпейді.'),
    (N'err.ECR-PRD-4225.offsetOutOfRange', N'ru', N'Смещение {field} ({value} дн.) должно быть от -{max} до {max} дн.'),
    (N'err.ECR-PRD-4225.offsetOutOfRange', N'kz', N'{field} ығысуы ({value} күн) -{max} мен {max} күн аралығында болуы тиіс.'),
    (N'err.ECR-PRD-0422.pinReasonTooLong', N'ru', N'Причина закрепления текущего периода длиннее {max} символов.'),
    (N'err.ECR-PRD-0422.pinReasonTooLong', N'kz', N'Ағымдағы кезеңді тіркеп қою себебі {max} таңбадан ұзын.'),
    (N'err.ECR-PWD-0422.tooLong', N'ru', N'Пароль длиннее {maxLength} символов.'),
    (N'err.ECR-PWD-0422.tooLong', N'kz', N'Құпиясөз {maxLength} таңбадан ұзын.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- API:negative-path ── кінець секції ──

