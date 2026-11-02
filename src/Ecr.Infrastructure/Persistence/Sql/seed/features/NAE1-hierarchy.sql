-- NAE1:hierarchy ── ru/kz ієрархічні довідники (рівні, дерево, переміщення, закриття вузла); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.levelRequired', N'ru', N'Запись иерархического справочника должна иметь уровень.'),
    (N'err.ECR-REG-0422.levelUnknown', N'ru', N'Уровень «{levelCode}» не объявлен в этом справочнике.'),
    (N'err.ECR-REG-0422.levelOrder', N'ru', N'Узел должен находиться на уровне глубже родительского.'),
    (N'err.ECR-REG-0422.levelSkipsRequired', N'ru', N'Уровни между родителем и этим узлом обязательны и не могут быть пропущены.'),
    (N'err.ECR-REG-0422.hierarchyCycle', N'ru', N'Запись {entryId} нельзя поместить под её собственного потомка.'),
    (N'err.ECR-REG-0422.hierarchyLevelsInvalid', N'ru', N'Список уровней некорректен: нужно от 1 до {max} уровней с уникальными кодами и английским названием.'),
    (N'err.ECR-REG-0422.hierarchyLevelChange', N'ru', N'Новый порядок уровней оставил бы {count} записей в недопустимом месте.'),
    (N'err.ECR-REG-0422.hierarchyNotEnabled', N'ru', N'Справочник «{registryCode}» не является иерархическим.'),
    (N'err.ECR-REG-0422.moveBeforeNotSibling', N'ru', N'Запись {entryId} не является дочерней для выбранного родителя.'),
    (N'err.ECR-REG-0409.levelInUse', N'ru', N'Уровень «{levelCode}» содержит записей: {count}, удалить его нельзя.'),
    (N'err.ECR-REG-0409.nodeHasLiveChildren', N'ru', N'У узла есть действующих потомков: {count}. Закройте и их (каскадно).'),
    (N'registryAudit.hierarchySaved', N'ru', N'Уровней: {count}.'),
    (N'registryAudit.entryMoved', N'ru', N'Перенумеровано соседних записей: {count}.'),
    (N'registryAudit.entryRetired', N'ru', N'Закрыто записей: {count}.'),
    (N'err.ECR-REG-0422.levelRequired', N'kz', N'Иерархиялық анықтамалықтың жазбасында деңгей болуы керек.'),
    (N'err.ECR-REG-0422.levelUnknown', N'kz', N'«{levelCode}» деңгейі бұл анықтамалықта жарияланбаған.'),
    (N'err.ECR-REG-0422.levelOrder', N'kz', N'Түйін өзінің аталық түйінінен терең деңгейде тұруы керек.'),
    (N'err.ECR-REG-0422.levelSkipsRequired', N'kz', N'Аталық түйін мен осы түйін арасындағы деңгейлер міндетті, оларды өткізіп жіберуге болмайды.'),
    (N'err.ECR-REG-0422.hierarchyCycle', N'kz', N'{entryId} жазбасын өзінің ұрпағының астына орналастыруға болмайды.'),
    (N'err.ECR-REG-0422.hierarchyLevelsInvalid', N'kz', N'Деңгейлер тізімі жарамсыз: бірегей кодтары мен ағылшынша атауы бар 1-ден {max}-ге дейін деңгей қажет.'),
    (N'err.ECR-REG-0422.hierarchyLevelChange', N'kz', N'Деңгейлердің жаңа реті {count} жазбаны жарамсыз орында қалдырар еді.'),
    (N'err.ECR-REG-0422.hierarchyNotEnabled', N'kz', N'«{registryCode}» анықтамалығы иерархиялық емес.'),
    (N'err.ECR-REG-0422.moveBeforeNotSibling', N'kz', N'{entryId} жазбасы таңдалған аталық түйіннің еншілесі емес.'),
    (N'err.ECR-REG-0409.levelInUse', N'kz', N'«{levelCode}» деңгейінде {count} жазба бар, оны жоюға болмайды.'),
    (N'err.ECR-REG-0409.nodeHasLiveChildren', N'kz', N'Түйіннің {count} әрекет ететін еншілесі бар: оларды да жабыңыз (каскадпен).'),
    (N'registryAudit.hierarchySaved', N'kz', N'Деңгейлер: {count}.'),
    (N'registryAudit.entryMoved', N'kz', N'Қайта нөмірленген көрші жазбалар: {count}.'),
    (N'registryAudit.entryRetired', N'kz', N'Жабылған жазбалар: {count}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE1:hierarchy ── кінець секції ──

