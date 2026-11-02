-- NAE2:af-import ── ru/kz разове завантаження дерева НАЕ з AF; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.naeTreeIncompatible', N'ru', N'Справочник «{registryCode}» уже существует, но не является деревом НАЕ (нет: {missing}).'),
    (N'err.ECR-REG-0422.naeImportRejected', N'ru', N'Загрузка НАЕ отклонена на записи {code}: {reason}.'),
    (N'registryAudit.naeAfImport', N'ru', N'Создано узлов НАЕ: {count}.'),
    (N'err.ECR-REG-0422.naeTreeIncompatible', N'kz', N'«{registryCode}» анықтамалығы бар, бірақ НАЕ ағашы емес (жоқ: {missing}).'),
    (N'err.ECR-REG-0422.naeImportRejected', N'kz', N'НАЕ жүктеу {code} жазбасында қабылданбады: {reason}.'),
    (N'registryAudit.naeAfImport', N'kz', N'Жасалған НАЕ түйіндері: {count}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE2:af-import ── кінець секції ──
