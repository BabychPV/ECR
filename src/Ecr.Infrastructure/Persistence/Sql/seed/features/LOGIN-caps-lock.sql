-- LOGIN:caps-lock ── ru/kz підказки Caps Lock на екрані входу (аудит 2026-11 U-9, UX-16); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'login.capsLockOn', N'ru', N'Включён Caps Lock'),
    (N'login.capsLockOn', N'kz', N'Caps Lock қосулы')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- LOGIN:caps-lock ── кінець секції ──
-- LOGIN:caps-lock ── en (публічна область — екран входу вантажить ?scope=public); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'login.capsLockOn', N'en', N'Caps Lock is on', 0)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- LOGIN:caps-lock ── кінець секції ──
