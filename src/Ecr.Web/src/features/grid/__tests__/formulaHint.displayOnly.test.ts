import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { buildRequest } from '../cellPatchRequest';

/**
 * `ФВ-14.5`: клієнтське значення формули — лише візуальна підказка; остаточне рахує сервер (`D-20`).
 *
 * ⚠ Клієнтський обчислювач `shared/formula/evaluate.ts` лишається — він половина доказу еквівалентності
 * клієнт↔сервер (`D-20`, `D-314`; спільна фікстура `expression-equivalence.json`). Тут закріплено інше: жоден
 * модуль, що будує чи надсилає `PATCH /documents/{id}/cells`, не бачить його навіть транзитивно, тож
 * пораховане клієнтом значення в запит потрапити не може — запит несе лише те, що ввела людина.
 *
 * ⛔ Мутаційний доказ (вручну, відновлено): додати в `settleEdits.ts` рядок
 * `import { evaluate } from '@/shared/formula/evaluate'; void evaluate;` — червоні `settleEdits.ts` і
 * транзитивно `autosave.ts`, `restoreEdits.ts`.
 */
const Src = path.resolve(process.cwd(), 'src');

/** Модулі шляху запису комірок: збирання запиту, відправка, автозбереження, діапазонні правки. */
const PatchPath = [
  'features/grid/cellPatchRequest.ts',
  'features/grid/useCellPatch.ts',
  'features/grid/autosave.ts',
  'features/grid/rangeEdit.ts',
  'features/grid/settleEdits.ts',
  'features/grid/restoreEdits.ts',
  'features/grid/pendingStore.ts',
];

const Evaluator = path.join(Src, 'shared', 'formula', 'evaluate.ts');

function resolveImport(from: string, spec: string): string | null {
  let base: string;
  if (spec.startsWith('@/')) base = path.join(Src, spec.slice(2));
  else if (spec.startsWith('.')) base = path.resolve(path.dirname(from), spec);
  else return null;

  for (const candidate of [base, `${base}.ts`, `${base}.tsx`, path.join(base, 'index.ts'), path.join(base, 'index.tsx')]) {
    if (existsSync(candidate) && /\.tsx?$/.test(candidate)) return candidate;
  }

  return null;
}

/** Транзитивне замикання статичних і динамічних імпортів від `entry`. */
function reachable(entry: string): Set<string> {
  const seen = new Set<string>();
  const stack = [entry];
  const pattern = /(?:from\s+|import\s*\(\s*|import\s+)['"]([^'"]+)['"]/g;

  while (stack.length > 0) {
    const file = stack.pop()!;
    if (seen.has(file)) continue;
    seen.add(file);

    const text = readFileSync(file, 'utf8');
    for (const match of text.matchAll(pattern)) {
      const next = resolveImport(file, match[1]!);
      if (next !== null && !seen.has(next)) stack.push(next);
    }
  }

  return seen;
}

describe('ФВ-14.5: клієнтське значення формули — лише підказка, у PATCH не їде', () => {
  it.each(PatchPath)('%s не досягає shared/formula/evaluate.ts навіть транзитивно', (relative) => {
    const entry = path.join(Src, relative);
    expect(existsSync(entry), `${relative} перейменовано — онови перелік PatchPath`).toBe(true);
    expect([...reachable(entry)]).not.toContain(Evaluator);
  });

  it('запит несе введене людиною значення формульної комірки без жодних обчислених полів', () => {
    const request = buildRequest(7, 202606, [
      { rowKey: 'r1', columnCode: 'Total', value: '=1+2', isEmpty: false, baseVersion: 'AAA=' },
    ]);

    expect(request.rows).toEqual([
      { rowKey: 'r1', baseVersion: 'AAA=', cells: [{ columnCode: 'Total', value: '=1+2', isEmpty: false }] },
    ]);
  });
});
