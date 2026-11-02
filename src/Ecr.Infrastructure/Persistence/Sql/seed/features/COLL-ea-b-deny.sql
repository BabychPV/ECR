-- COLL:ea-b-deny ── ru/kz: EA-B: причини відмови запису від «передачі місяця»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'deny.LockedByOwner', N'ru', N'Владелец документа передал эту часть назначенным пользователям. Пока передача не валидирована или не снята, править её могут только владелец и эти пользователи.'),
    (N'deny.LockedByOwner', N'kz', N'Құжат иесі бұл бөлікті тағайындалған пайдаланушыларға тапсырды. Тапсыру расталмайынша немесе алынбайынша, оны тек иесі мен осы пайдаланушылар өзгерте алады.'),
    (N'deny.AssignmentFilled', N'ru', N'Вы отметили эту часть как заполненную, поэтому для вас она закрыта. Править её снова можно, только если владелец вернёт её на доработку.'),
    (N'deny.AssignmentFilled', N'kz', N'Сіз бұл бөлікті толтырылды деп белгілегенсіз, сондықтан ол сіз үшін жабық. Оны қайта түзету үшін иесі қайта өңдеуге қайтаруы керек.'),
    (N'deny.AssignmentValidated', N'ru', N'Владелец валидировал эту часть, поэтому она закрыта для правок. Если нужно исправление, попросите владельца вернуть её.'),
    (N'deny.AssignmentValidated', N'kz', N'Иесі бұл бөлікті растады, сондықтан ол түзетуге жабық. Түзету қажет болса, иесінен оны қайтаруды сұраңыз.'),
    (N'deny.AssignmentExpired', N'ru', N'Срок передачи этой части истёк, поэтому для вас она закрыта. Попросите владельца продлить или повторить передачу.'),
    (N'deny.AssignmentExpired', N'kz', N'Бұл бөлікті тапсыру мерзімі аяқталды, сондықтан ол сіз үшін жабық. Иесінен тапсыруды ұзартуды немесе қайталауды сұраңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ea-b-deny ── кінець секції ──

-- COLL:ea-b-deny ── EA-B: причини відмови запису від «передачі місяця» (LockedByOwner/AssignmentFilled/AssignmentValidated); без імен інших людей (R9); ru/kz — порцією COLL:ea-b-deny нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'deny.LockedByOwner', N'en', N'The document owner handed this part to assigned users. Until the handover is validated or removed, only the owner and those users can edit it.', 1),
    (N'deny.AssignmentFilled', N'en', N'You marked this part as filled, so it is closed for you. You can edit it again only if the owner returns it for rework.', 1),
    (N'deny.AssignmentValidated', N'en', N'The owner validated this part, so it is closed for editing. Ask the owner to return it if a correction is needed.', 1),
    (N'deny.AssignmentExpired', N'en', N'The handover period for this part has expired, so it is closed for you. Ask the owner to extend or renew the handover.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ea-b-deny ── кінець секції ──
