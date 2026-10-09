# L7/L10 recheck @3a3818ef (agent V2 a8e72d59). L5/L6 pending from subagents a924c1b9 / ab5e92b3.
L7-01 critical PARTIAL (VERIFY MYSELF): 
 1) Parser.cs:1427-1431 LeaveNesting resets _chainLinks = _chainLinksAtEntry[_depth] -> left-paren chains bypass MaxChainLinks=1024; depth = sum; ((((1+..)+1..)..)) ~63x1024 = 64k depth, ~130KB. Test only right parens. XML doc :1397 false.
 2) Parser.cs:1278-1299 InferShape recursive, called in ParseUncached :317 before any guard; AstTraversalRecursionCoverageTests.cs:46-48 excludes Parser.cs (OwnGuardFiles); ParserRecursionCoverageTests.cs:170 only Parse*(State).
 3) No length guard: MethodologyCategoryRuleHandlers.cs:188 RequireUsable -> formulaEngine.Parse (Calculation.EditRule), calc.CategoryRule.Expression no HasMaxLength (CalculationsConfiguration.cs:429), MethodologiesController.cs:361 no RequestSizeLimit; ReportRowRules.cs:213 new Parser().Parse (ReportDefHandlers.cs:185,362,466, Report.EditDefinition).
 Fix: EnsureSufficientExecutionStack / iterative InferShape; remove Parser.cs from OwnGuardFiles; carry inner chain depth when left operand parens; ExpressionLengthGuard.Require in RequireUsable + HasMaxLength migration; ReportRowRules guard. Test: Parse("("+chain(600)+")"+Repeat("+1",600)) -> chainTooLong; process probe.
L7-02..12 FIXED; L7-13 N/A D-283. Tails: L7-04 P3-6 reimport corpus with &; L7-07 P3-5 stand check; L7-10 old bad golden key -> InvalidOperationException 500 GoldenSet.cs:190-195; L7-11 no period -> month key. P3-2 actually closed (FunctionRegistry.cs:141, 895e0d47). L7-08 lane line in WQ stale.
L10 FIXED: 01,03,06,07,08,10,12,14,15. PARTIAL:
 L10-02 Folders.wxs:73-84 logs inherits Users:Write -> PermissionEx SDDL + icacls svc M + verify-msi Assert-NoForeignWrite.
 L10-04 DeployArguments.cs:126 TrustServerCertificate=True -> wizard checkbox default off or D-282 note.
 L10-05 DeployWorkerModeTests.cs:248-251 uses pwsh if present -> not red; add Windows Fact via powershell.exe 5.1.
 L10-11 honesty-guard.sh:52-54 Skip only with = same line.
 L10-13 setup-dev-db.ps1:343-350, verify-sql-scripts.ps1:134 drop DB without marker.
 L10-16 runbook §8.2 no offline window for Standard; ADD NOT NULL DEFAULT (IsOutOfWindow, calc.CalculationResult.Kind).
 OPEN L10-09 ci.yml:98,226,239,268,324,352 evidence.yml:57,76,105 skipped=success for head_ref dev/integration, no guard.
 Others: L10-01 no arch guard; L10-06 AnyAsync no lock -> 547 race; L10-15 D256 Down 4000->2000 would fail on live formulas.
WQ: AN-25..AN-38/41 todo though committed.
COORDINATOR VERIFIED L7-01 bypass (1): Parser.cs:353-358 ParseExpression Enter/LeaveNesting around parens; LeaveNesting :1427-1431 restores _chainLinks to entry value, so after "(chain)" returns outer chain counter = value before paren (0) -> outer chain gets full 1024 again while paren subtree is its left spine. InferShape :1278-1299 recursive on Left, called :317 before any guard. Confirmed statically.
