import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';

/**
 * L10-11 (AUDIT-2026-10-03 §1J): вимкнений тест у клієнті не проходить гейт `client`.
 *
 * ⛔ Навіщо. `honesty-guard` дивиться лише на `*.cs`, тож `it.skip(...)`,
 * `describe.only(...)` чи `test.todo(...)` у клієнті давали зелений набір без
 * доказу — і ніхто цього не ловив. `npm run lint` у CI не запускається, тому
 * правило ESLint тут нічого б не гарантувало: перевірка — тест, і він іде в
 * `client` на кожен пуш.
 *
 * Дозволено рівно одне: умовний `test.skip(<умова>, 'причина')` Playwright у
 * `e2e/` — ним прогони пропускаються без стенда (`ECR_E2E_OPTIONAL`). Перший
 * аргумент там — вираз, а не назва тесту. `test.skip('назва', ...)` — вимкнений
 * тест, і він заборонений так само, як `.only`, `.todo` і `.fixme`.
 */

const webRoot = path.resolve(process.cwd());

// Збирається зі шматків, щоб текст сторожа не ловив сам себе.
const Skip = 'sk' + 'ip';
const Modifiers = [Skip, 'on' + 'ly', 'to' + 'do', 'fix' + 'me', Skip + 'If', 'run' + 'If'].join('|');
/*
 * Ланцюжок будь-якої довжини з пробілами й переносами між ланками: `it.skip.each(...)`,
 * `it.concurrent.skip(...)`, `test.describe.parallel.skip(...)`, `it\n  .skip(...)`.
 * `skipIf`/`runIf` заборонені цілком: умовний пропуск поза Playwright не потрібен, а
 * `skipIf(true)` — безумовний. Плюс префікси `xit`/`fit` у стилі Jasmine.
 */
const Disabled = new RegExp(
  String.raw`\b(it|test|describe|bench)((?:\s*\.\s*[A-Za-z_$][\w$]*)*?)\s*\.\s*(${Modifiers})\b` +
    String.raw`|\b(x(?:it|test|describe)|f(?:it|describe))\s*\(`,
  'g',
);

interface DisabledTest {
  readonly line: number;
  readonly text: string;
}

/** Коментарі й вміст рядкових літералів — пробіли (переноси лишаються, номери рядків не зсуваються). */
function blankComments(source: string): string {
  let out = '';
  let i = 0;
  while (i < source.length) {
    const c = source[i];
    const next = source[i + 1];
    if (c === '/' && next === '/') {
      while (i < source.length && source[i] !== '\n') { out += ' '; i++; }
    } else if (c === '/' && next === '*') {
      const close = source.indexOf('*/', i + 2);
      const stop = close < 0 ? source.length : close + 2;
      out += source.slice(i, stop).replace(/[^\n]/g, ' ');
      i = stop;
    } else if (c === '"' || c === "'" || c === '`') {
      // Лапки лишаються (по них e2e відрізняє назву від умови), вміст — пробіли.
      const stop = stringEnd(source, i);
      out += c + source.slice(i + 1, stop - 1).replace(/[^\n]/g, ' ') + source.slice(stop - 1, stop);
      i = stop;
    } else { out += c; i++; }
  }

  return out;
}

/** Індекс одразу за рядковим літералом, що починається на `start` (вкладені `${}` шаблону — без розбору). */
function stringEnd(source: string, start: number): number {
  const quote = source[start];
  let i = start + 1;
  while (i < source.length && source[i] !== quote) {
    if (source[i] === '\\') i++;
    else if (source[i] === '\n' && quote !== '`') break;
    i++;
  }

  return i + 1;
}

/** Аргументи виклику верхнього рівня, починаючи з `(` на `open`; `undefined` — дужки немає. */
function callArgs(source: string, open: number): string[] | undefined {
  if (source[open] !== '(') return undefined;
  const args: string[] = [];
  let depth = 0;
  let current = '';
  let i = open + 1;
  while (i < source.length) {
    const c = source[i];
    if (c === '"' || c === "'" || c === '`') {
      const stop = stringEnd(source, i);
      current += source.slice(i, stop);
      i = stop;
      continue;
    }
    if (c === '(' || c === '[' || c === '{') depth++;
    if (c === ')' || c === ']' || c === '}') {
      if (depth === 0) break;
      depth--;
    }
    if (c === ',' && depth === 0) { args.push(current.trim()); current = ''; }
    else current += c;
    i++;
  }
  if (current.trim() !== '') args.push(current.trim());

  return args;
}

/*
 * Єдина дозволена форма в e2e/: `test.skip(<умова>, 'причина')` Playwright — рівно два
 * аргументи, перший — вираз (не літерал рядка і не `true`). `test.skip()` і
 * `test.skip(true, …)` — безумовне вимкнення, `test.skip('назва', …)` — вимкнений тест.
 */
function isConditionalPlaywrightSkip(code: string, match: RegExpMatchArray): boolean {
  if (match[1] !== 'test' || match[2] !== '' || match[3] !== Skip) return false;
  const after = (match.index ?? 0) + match[0].length;
  const open = after + (code.slice(after).length - code.slice(after).trimStart().length);
  const args = callArgs(code, open);
  if (args === undefined || args.length !== 2) return false;
  const condition = args[0] ?? '';

  return condition !== '' && condition !== 'true' && !/^['"`]/.test(condition);
}

/** Порушення в тексті одного файлу; `e2e` — правила Playwright (умовний skip дозволено). */
function findDisabledTests(source: string, e2e: boolean): DisabledTest[] {
  const code = blankComments(source);
  const lines = source.split('\n');
  const found: DisabledTest[] = [];
  for (const match of code.matchAll(Disabled)) {
    if (e2e && isConditionalPlaywrightSkip(code, match)) continue;
    const line = code.slice(0, match.index).split('\n').length;
    found.push({ line, text: (lines[line - 1] ?? '').trim() });
  }

  return found;
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === 'node_modules' ? [] : sourceFiles(full);

    return /\.(ts|tsx)$/.test(entry.name) && !entry.name.endsWith('.d.ts') ? [full] : [];
  });
}

describe('L10-11 — вимкнений тест у клієнті не проходить', () => {
  it('у src/ і e2e/ немає it/test/describe зі skip/only/todo/fixme', () => {
    const violations = (['src', 'e2e'] as const).flatMap((top) =>
      sourceFiles(path.join(webRoot, top)).flatMap((file) =>
        findDisabledTests(readFileSync(file, 'utf8'), top === 'e2e').map(
          (v) => `${path.relative(webRoot, file)}:${v.line}: ${v.text}`,
        ),
      ),
    );

    expect(violations).toEqual([]);
  });

  it.each([
    [`it.${'sk' + 'ip'}('рахує суму', () => {});`, false],
    [`describe.${'on' + 'ly'}("блок", () => {});`, false],
    [`test.${'to' + 'do'}(\`ще напишу\`);`, false],
    [`test.describe.${'sk' + 'ip'}('e2e блок', () => {});`, true],
    [`test.${'fix' + 'me'}(true, 'причина');`, true],
    [`test.${'sk' + 'ip'}('назва', async () => {});`, true],
    [`test.${'sk' + 'ip'}(\n  'назва',\n  async () => {},\n);`, true],
    // L10-11, рев'ю 04.10: п'ять форм, які проходили повз сторож.
    [`it.${'sk' + 'ip'}.each([1, 2])('рахує %i', () => {});`, false],
    [`test.${'on' + 'ly'}.each([1])('рахує %i', () => {});`, false],
    [`it.${'sk' + 'ip'}If(true)('рахує', () => {});`, false],
    [`it.${'run' + 'If'}(false)('рахує', () => {});`, false],
    [`it.concurrent.${'sk' + 'ip'}('рахує', async () => {});`, false],
    [`describe.sequential.${'sk' + 'ip'}('блок', () => {});`, false],
    [`test.describe.parallel.${'sk' + 'ip'}('e2e блок', () => {});`, true],
    [`it\n  .${'sk' + 'ip'}('рахує', () => {});`, false],
    [`test('сценарій', async () => {\n  test.${'sk' + 'ip'}();\n});`, true],
    [`test.${'sk' + 'ip'}(true, 'причина');`, true],
    [`test.${'sk' + 'ip'}(PeriodKey === '');`, true],
    [`x${'it'}('рахує', () => {});`, false],
  ])('ловить %j', (code, e2e) => {
    expect(findDisabledTests(code, e2e)).toHaveLength(1);
  });

  it.each([
    [`test.${'sk' + 'ip'}(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає.');`],
    [`test.${'sk' + 'ip'}(\n  PeriodKey === '' || DocumentId === '',\n  'ECR_E2E_OPTIONAL',\n);`],
    [`// Той самий гейт, що й у \`test.${'sk' + 'ip'}\` вище`],
    [`/**\n * через \`test.${'sk' + 'ip'}\`, і відсутність стенда\n */`],
    [`const hint = 'див. test.${'sk' + 'ip'}(...)';`],
  ])('умовний skip Playwright і коментар у e2e — дозволені: %j', (code) => {
    expect(findDisabledTests(code, true)).toEqual([]);
  });

  it('умовний skip поза e2e/ не дозволено: vitest такої форми не має', () => {
    expect(findDisabledTests(`it.${'sk' + 'ip'}(cond, 'причина');`, false)).toHaveLength(1);
  });
});
