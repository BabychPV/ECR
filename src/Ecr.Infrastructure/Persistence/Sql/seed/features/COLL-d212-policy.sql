-- COLL:d212-policy ── ru/kz політики синку довідника (D-212 PR-2); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.registrySyncPolicyInvalid', N'ru', N'Политика синхронизации: неизвестное значение, либо имя атрибута пустое или длиннее {max} символов; включительная граница требует атрибута окончания.'),
    (N'err.ECR-REQ-0422.registrySyncPolicyInvalid', N'kz', N'Синхрондау саясаты: белгісіз мән немесе атрибут атауы бос не {max} таңбадан ұзын; қоса алынатын шекара аяқталу атрибутын талап етеді.'),
    (N'err.ECR-REQ-0422.registrySyncPolicyNotBound', N'ru', N'Сущность сбора {id} не привязана к справочнику: политике синхронизации не к чему применяться.'),
    (N'err.ECR-REQ-0422.registrySyncPolicyNotBound', N'kz', N'{id} жинау нысаны анықтамалыққа байланыстырылмаған: синхрондау саясатын қолданатын нысан жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d212-policy ── кінець секції ──

