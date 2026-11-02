/**
 * Зміст довідки синтаксису формул методології — ДАНІ, а не розмітка.
 *
 * ⛔ Кожне твердження тут виведене з реалізації, а не з документа:
 * `Ecr.Expressions/Lexing/Lexer.cs` (лексеми, `@` з крапками в імені, рядок з
 * подвоєною лапкою), `Parsing/Parser.cs` (пріоритети, `?:`, `!` — посилання на
 * формулу лише перед ім'ям без `(` — `State.IsFormulaRef`; заборони `^`, `&`,
 * голого імені й посилань на комірки), `DialectSyntax.cs` (кома — роздільник,
 * `^` не степінь, `&` не конкатенація), `Evaluation/Evaluator.cs` (властивості
 * `[Period].X`). Перелік ФУНКЦІЙ тут НЕ зашитий — довідка бере його з сервера
 * (`/api/v1/expressions/metadata`, `DialectCatalog`), як і редактор.
 *
 * ⚠ Приклади — не ілюстрації, а перевірювані вирази: `formulaSyntax.test.ts`
 * проганяє кожен крізь лексику редактора (`buildMonarchLanguage`), а e2e
 * `methodologyUi.spec.ts` — крізь справжній розбір сервера
 * (`POST /api/v1/expressions/validate`, діалект `Methodology`). Приклади з
 * `ForbiddenExamples` там же мусять дати зауваження.
 */

/** Приклад формули: що написано і ідентифікатор для `data-example`. */
export interface FormulaExample {
  readonly id: string;
  readonly code: string;
}

/** Приклади, які сервер ПРИЙМАЄ в діалекті методологій (режим редактора). */
export const SyntaxExamples: readonly FormulaExample[] = [
  { id: 'argConstant', code: '@FuelUse * CST.NCV / 1000' },
  { id: 'dotted', code: '@GCV.Methane * 0.5' },
  { id: 'formulaRefs', code: '!E_CO2 + !E_CH4 * CST.GWP_CH4' },
  { id: 'ternary', code: '@Volume > 0 ? @Mass / @Volume : 0' },
  { id: 'if', code: "if(@Kind = 'Gas', @Mass * CST.EF_GAS, 0)" },
  { id: 'not', code: 'if(!@IsPilot, @Total, 0)' },
  { id: 'pow', code: 'Pow(@Rate, 2)' },
  { id: 'round', code: 'Round(@Mass * CST.Factor, 3)' },
  { id: 'in', code: "in(@Kind, 'Gas', 'Oil')" },
  { id: 'period', code: '[Period].Days * 24' },
  { id: 'modulo', code: '@Total % 7' },
  { id: 'logic', code: '@A >= 10 and @B <> 0' },
];

/** Вирази, які сервер ВІДХИЛЯЄ в діалекті методологій — кожен зі своєї причини. */
export const ForbiddenExamples: readonly FormulaExample[] = [
  { id: 'caret', code: '2 ^ 3' },
  { id: 'ampersand', code: "'a' & 'b'" },
  { id: 'bareName', code: 'Total * 2' },
  { id: 'cellRef', code: '[Sheet1].[R1].[C1]' },
  { id: 'caseSensitive', code: 'POW(2, 3)' },
];

/** Властивості календарного контексту `[Period].X` — дослівно `Evaluator.Period`. */
export const PeriodProperties = ['Days', 'Hours', 'Seconds', 'Start', 'End', 'Year', 'Sequence'] as const;
