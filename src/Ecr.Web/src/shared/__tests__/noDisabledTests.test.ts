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
const Modifiers = ['sk' + 'ip', 'on' + 'ly', 'to' + 'do', 'fix' + 'me'].join('|');
const Disabled = new RegExp(
  String.raw`\b(it|test|describe)(?:\.describe)?\.(${Modifiers})\s*\(\s*(\S?)`,
  'g',
);

interface DisabledTest {
  readonly line: number;
  readonly text: string;
}

/** Порушення в тексті одного файлу; `e2e` — правила Playwright (умовний skip дозволено). */
function findDisabledTests(source: string, e2e: boolean): DisabledTest[] {
  const found: DisabledTest[] = [];
  source.split('\n').forEach((raw, index) => {
    const trimmed = raw.trim();
    // Коментарі лише ЗГАДУЮТЬ `test.skip` — це не виклик.
    if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return;
    for (const match of raw.matchAll(Disabled)) {
      const modifier = match[2];
      const firstChar = match[3] ?? '';
      // Порожній перший аргумент у рядку — умова на наступному рядку (так пише Playwright-код тут).
      const titled = firstChar === '' ? nextArgIsTitle(source, index) : /['"`]/.test(firstChar);
      const conditionalSkip = e2e && modifier === Modifiers.split('|')[0] && !titled;
      if (!conditionalSkip) found.push({ line: index + 1, text: trimmed });
    }
  });

  return found;
}

function nextArgIsTitle(source: string, index: number): boolean {
  const next = source.split('\n').slice(index + 1).find((l) => l.trim() !== '');

  return next !== undefined && /^['"`]/.test(next.trim());
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
  ])('ловить %j', (code, e2e) => {
    expect(findDisabledTests(code, e2e)).toHaveLength(1);
  });

  it.each([
    [`test.${'sk' + 'ip'}(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає.');`],
    [`test.${'sk' + 'ip'}(\n  PeriodKey === '' || DocumentId === '',\n  'ECR_E2E_OPTIONAL',\n);`],
    [`// Той самий гейт, що й у \`test.${'sk' + 'ip'}\` вище`],
    [` * через \`test.${'sk' + 'ip'}\`, і відсутність стенда`],
  ])('умовний skip Playwright і коментар у e2e — дозволені: %j', (code) => {
    expect(findDisabledTests(code, true)).toEqual([]);
  });

  it('умовний skip поза e2e/ не дозволено: vitest такої форми не має', () => {
    expect(findDisabledTests(`it.${'sk' + 'ip'}(cond, 'причина');`, false)).toHaveLength(1);
  });
});
