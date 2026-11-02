-- COLL:r6x5-windows-sid ── ru/kz: R6-X5 / X5-01: SID доменного запису — відмова входу, перевірка, виправлення; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-USR-0409.windowsSidMismatch', N'ru', N'Учётная запись ECR «{userName}» привязана к другому SID Windows, поэтому вход отклонён. Попросите администратора исправить SID этой учётной записи на {sid}.'),
    (N'err.ECR-USR-0409.windowsSidMismatch', N'kz', N'ECR-дегі «{userName}» тіркелгісі басқа Windows SID-іне байланған, сондықтан кіру қабылданбады. Әкімшіден осы тіркелгінің SID-ін {sid} етіп түзетуді сұраңыз.'),
    (N'err.ECR-USR-0409.windowsSidMismatch.title', N'ru', N'Учётная запись привязана к другому SID'),
    (N'err.ECR-USR-0409.windowsSidMismatch.title', N'kz', N'Тіркелгі басқа SID-ке байланған'),
    (N'err.ECR-USR-0409.windowsSidTaken', N'ru', N'SID {sid} уже привязан к учётной записи «{userName}».'),
    (N'err.ECR-USR-0409.windowsSidTaken', N'kz', N'{sid} SID-і «{userName}» тіркелгісіне бұрыннан байланған.'),
    (N'err.ECR-USR-0409.windowsSidTaken.title', N'ru', N'SID уже используется'),
    (N'err.ECR-USR-0409.windowsSidTaken.title', N'kz', N'SID бұрыннан қолданылуда'),
    (N'err.ECR-USR-0422.windowsSidMalformed', N'ru', N'«{sid}» не является SID. Введите его в виде S-1-5-21-….'),
    (N'err.ECR-USR-0422.windowsSidMalformed', N'kz', N'«{sid}» SID емес. Оны S-1-5-21-… түрінде енгізіңіз.'),
    (N'err.ECR-USR-0422.windowsSidNotDomain', N'ru', N'«{userName}» — не доменная учётная запись: SID у неё нет.'),
    (N'err.ECR-USR-0422.windowsSidNotDomain', N'kz', N'«{userName}» — домендік тіркелгі емес: оның SID-і жоқ.'),
    (N'err.ECR-SEC-0409.windowsSidConfirmed', N'ru', N'«{userName}» уже входил в систему: домен подтвердил его SID, поэтому исправить его нельзя.'),
    (N'err.ECR-SEC-0409.windowsSidConfirmed', N'kz', N'«{userName}» жүйеге кірген: домен оның SID-ін растады, сондықтан оны түзетуге болмайды.'),
    (N'security.correctSid', N'ru', N'Исправить SID'),
    (N'security.correctSid', N'kz', N'SID-ті түзету'),
    (N'security.correctSidNamed', N'ru', N'Исправить SID {userName}'),
    (N'security.correctSidNamed', N'kz', N'{userName} SID-ін түзету'),
    (N'security.correctSidHint', N'ru', N'Эта доменная учётная запись ещё не входила в систему, поэтому её SID можно исправить. Введите SID учётной записи в домене; он указан в отказе во входе, который видит сотрудник.'),
    (N'security.correctSidHint', N'kz', N'Бұл домендік тіркелгі жүйеге әлі кірмеген, сондықтан оның SID-ін түзетуге болады. Тіркелгінің домендегі SID-ін енгізіңіз; ол қызметкер көретін кіруден бас тарту хабарында көрсетілген.'),
    (N'security.sidLabel', N'ru', N'SID'),
    (N'security.sidLabel', N'kz', N'SID'),
    (N'security.sidCorrected', N'ru', N'SID исправлен'),
    (N'security.sidCorrected', N'kz', N'SID түзетілді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r6x5-windows-sid ── кінець секції ──

