import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * P3 Аудиту-2 (ui-sheet-focus): CSS журналу аудиту, панелі дій документа і гамбургера — без `!important`,
 * журнал аудиту переносить слова цілими (`break-word`), а не `anywhere` (різав «administ/rator»).
 */
const Root = resolve(process.cwd(), 'src');
const read = (path: string): string => readFileSync(resolve(Root, path), 'utf8');

/** Без коментарів: у пояснюваннях слово `!important` дозволено. */
const code = (path: string): string => read(path).replace(/\/\*[\s\S]*?\*\//g, '');

describe('CSS без !important', () => {
  it.each(['features/audit/auditTable.css', 'features/documents/documentToolbar.css', 'app/narrowShell.css'])(
    '%s',
    (path) => {
      expect(code(path)).not.toContain('!important');
    },
  );
});

describe('журнал аудиту: перенос слів', () => {
  it('break-word + word-break: normal; `anywhere` лише для колонки «Було → стало»', () => {
    const css = code('features/audit/auditTable.css');

    expect(css).toContain('overflow-wrap: break-word');
    expect(css).toContain('word-break: normal');
    expect(css.match(/anywhere/g)?.length).toBe(1);
    expect(css).toMatch(/:has\(\[data-audit-was-becomes\]\)\s*\{[^}]*anywhere/);
  });
});
