-- IDLE:subtitle-retention ── змінений текст наявного ключа: підзаголовок вкладки «Session» тепер згадує й строки зберігання журналів та аудиту (PSS-13/24/27, П-3). Нові бази беруть нове значення з IDLE-signout.sql; епілог оновлює наявні, лише поки там СТАРЕ значення ──
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'security.subtitle.session', N'en', N'How long a signed-in person can stay inactive.', N'How long a signed-in person can stay inactive, and how long logs and audit records are kept.'),
    (N'security.subtitle.session', N'ru', N'Как долго вошедший пользователь может бездействовать.', N'Как долго вошедший пользователь может бездействовать и сколько хранятся журналы и аудит.'),
    (N'security.subtitle.session', N'kz', N'Кірген пайдаланушы қанша уақыт әрекетсіз бола алады.', N'Кірген пайдаланушы қанша уақыт әрекетсіз бола алады және журналдар мен аудит қанша сақталады.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- IDLE:subtitle-retention ── кінець секції ──
