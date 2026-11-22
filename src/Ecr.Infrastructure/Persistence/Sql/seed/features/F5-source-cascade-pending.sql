-- F5:source-cascade-pending ── ru/kz: подання й затвердження місяця споживача, поки його чекає каскад від зміни джерела HSE400 (D-566); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SUB-4221.sourceCascadePending', N'ru', N'Лист нельзя подать или утвердить: идёт пересчёт после изменения источника (HSE400) — попробуйте через минуту.'),
    (N'err.ECR-SUB-4221.sourceCascadePending', N'kz', N'Парақты тапсыруға немесе бекітуге болмайды: дереккөз (HSE400) өзгергеннен кейін қайта есептеу жүріп жатыр — бір минуттан соң қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- F5:source-cascade-pending ── кінець секції ──

-- F5:source-cascade-pending ── en: відмова подання й затвердження, поки місяць чекає каскаду джерела (SourceCascadePendingCheck); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SUB-4221.sourceCascadePending', N'en', N'The sheet cannot be submitted or approved: recalculation after a change of the source (HSE400) is in progress — try again in a minute.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- F5:source-cascade-pending ── кінець секції ──
