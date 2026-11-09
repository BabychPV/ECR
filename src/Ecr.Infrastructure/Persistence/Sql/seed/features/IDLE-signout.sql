-- COLL:idle-signout ── ru/kz: D-471 вихід після бездіяльності (налаштування «Безпека», попередження, сторінка входу) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-AUTH-0401.idleTimeout', N'ru', N'Сеанс завершён после {idleMinutes} мин без действий. Войдите снова.'),
    (N'err.ECR-AUTH-0401.idleTimeout', N'kz', N'Сеанс {idleMinutes} мин әрекетсіздіктен кейін аяқталды. Қайта кіріңіз.'),
    (N'err.ECR-REQ-0422.idleTimeoutOutOfRange', N'ru', N'Выход после бездействия: от {min} до {max} минут.'),
    (N'err.ECR-REQ-0422.idleTimeoutOutOfRange', N'kz', N'Әрекетсіздіктен кейін шығу: {min} мен {max} минут аралығында.'),
    (N'login.idleSignedOut', N'ru', N'Вы вышли из системы после {minutes} мин без действий. Войдите снова.'),
    (N'login.idleSignedOut', N'kz', N'Сіз {minutes} мин әрекетсіздіктен кейін жүйеден шықтыңыз. Қайта кіріңіз.'),
    (N'login.idleSignedOutNoMinutes', N'ru', N'Вы вышли из системы из-за бездействия. Войдите снова.'),
    (N'login.idleSignedOutNoMinutes', N'kz', N'Сіз әрекетсіздікке байланысты жүйеден шықтыңыз. Қайта кіріңіз.'),
    (N'idle.warning.title', N'ru', N'Вы ещё здесь?'),
    (N'idle.warning.title', N'kz', N'Сіз әлі осындасыз ба?'),
    (N'idle.warning.text', N'ru', N'Из-за бездействия вы скоро выйдете из системы. Несохранённые изменения будут сохранены для восстановления после входа.'),
    (N'idle.warning.text', N'kz', N'Әрекетсіздікке байланысты жақында жүйеден шығасыз. Сақталмаған өзгерістер кіргеннен кейін қалпына келтіру үшін сақталады.'),
    (N'idle.warning.remaining', N'ru', N'Осталось до выхода'),
    (N'idle.warning.remaining', N'kz', N'Шығуға дейін қалды'),
    (N'idle.warning.announce', N'ru', N'Выход через {time}'),
    (N'idle.warning.announce', N'kz', N'{time} кейін шығу'),
    (N'idle.warning.stay', N'ru', N'Остаться в системе'),
    (N'idle.warning.stay', N'kz', N'Жүйеде қалу'),
    (N'security.session', N'ru', N'Сеанс'),
    (N'security.session', N'kz', N'Сеанс'),
    (N'security.subtitle.session', N'ru', N'Как долго вошедший пользователь может бездействовать.'),
    (N'security.subtitle.session', N'kz', N'Кірген пайдаланушы қанша уақыт әрекетсіз бола алады.'),
    (N'security.session.idleLabel', N'ru', N'Выход после бездействия'),
    (N'security.session.idleLabel', N'kz', N'Әрекетсіздіктен кейін шығу'),
    (N'security.session.idleHint', N'ru', N'От {min} до {max} минут, по умолчанию {default}.'),
    (N'security.session.idleHint', N'kz', N'{min} мен {max} минут аралығында, әдепкі {default}.'),
    (N'security.session.idleExplain', N'ru', N'Считаются только действия человека: щелчки, клавиши, прокрутка. Фоновые опросы сеанс не продлевают. За минуту до выхода появляется предупреждение. Новое значение действует для всех открытых сеансов без перезапуска.'),
    (N'security.session.idleExplain', N'kz', N'Тек адам әрекеттері есептеледі: шертулер, пернелер, айналдыру. Фондық сұраулар сеансты ұзартпайды. Шығуға бір минут қалғанда ескерту шығады. Жаңа мән барлық ашық сеанстарға қайта іске қоспай-ақ қолданылады.'),
    (N'security.session.minutesSuffix', N'ru', N'мин'),
    (N'security.session.minutesSuffix', N'kz', N'мин'),
    (N'security.session.save', N'ru', N'Сохранить'),
    (N'security.session.save', N'kz', N'Сақтау'),
    (N'security.session.saved', N'ru', N'Выход после бездействия: {minutes} мин.'),
    (N'security.session.saved', N'kz', N'Әрекетсіздіктен кейін шығу: {minutes} мин.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:idle-signout ── кінець секції ──

-- COLL:idle-signout ── D-471: en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-AUTH-0401.idleTimeout', N'en', N'You were signed out after {idleMinutes} min without activity. Sign in again.', 1),
    (N'err.ECR-REQ-0422.idleTimeoutOutOfRange', N'en', N'Sign-out after inactivity must be between {min} and {max} minutes.', 1),
    (N'login.idleSignedOut', N'en', N'You were signed out after {minutes} min of inactivity. Sign in again.', 0),
    (N'login.idleSignedOutNoMinutes', N'en', N'You were signed out because of inactivity. Sign in again.', 0),
    (N'idle.warning.title', N'en', N'Are you still there?', 1),
    (N'idle.warning.text', N'en', N'You will be signed out soon because of inactivity. Unsaved changes will be kept for you to restore after you sign in.', 1),
    (N'idle.warning.remaining', N'en', N'Time left before sign-out', 1),
    (N'idle.warning.announce', N'en', N'Signing out in {time}', 1),
    (N'idle.warning.stay', N'en', N'Stay signed in', 1),
    (N'security.session', N'en', N'Session', 1),
    (N'security.subtitle.session', N'en', N'How long a signed-in person can stay inactive.', 1),
    (N'security.session.idleLabel', N'en', N'Sign out after N min without activity', 1),
    (N'security.session.idleHint', N'en', N'From {min} to {max} minutes, default {default}.', 1),
    (N'security.session.idleExplain', N'en', N'Only a person''s actions count: clicks, keys, scrolling. Background polling does not extend the session. A warning appears one minute before sign-out. A new value applies to all open sessions without a restart.', 1),
    (N'security.session.minutesSuffix', N'en', N'min', 1),
    (N'security.session.save', N'en', N'Save', 1),
    (N'security.session.saved', N'en', N'Sign-out after inactivity: {minutes} min.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:idle-signout ── кінець секції ──
