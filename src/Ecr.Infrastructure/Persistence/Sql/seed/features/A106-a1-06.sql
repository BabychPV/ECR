-- A1-06 ── ru/kz довжини маршруту погодження; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-DOC-0422.approvalRouteTooLong', N'ru', N'В маршруте согласования может быть не более {max} шагов, а в этом — {count}.'),
    (N'err.ECR-DOC-0422.approvalRouteTooLong', N'kz', N'Келісу маршрутында {max} қадамнан артық болмауы керек, ал мұнда — {count}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- A1-06 ── кінець секції ──

-- A1-06 ── Довжина маршруту погодження обмежена (`ReplaceApprovalRouteHandler.MaxSteps`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-DOC-0422.approvalRouteTooLong', N'en', N'An approval route can have at most {max} steps; this one has {count}.', 1)
    -- ru/kz — окремою порцією `A1-06` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- A1-06 ── кінець секції ──
