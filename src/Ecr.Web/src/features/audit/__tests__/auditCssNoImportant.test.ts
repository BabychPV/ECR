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
  // ✎ P-1124-4: другий `anywhere` — лише технічні ідентифікатори (`[data-audit-technical]`: GUID кореляції, задача),
  // не слова: розрив усередині них нічого не ріже, а без нього GUID розпирав таблицю.
  it('break-word + word-break: normal; `anywhere` лише для «Було → стало» і технічних ідентифікаторів', () => {
    const css = code('features/audit/auditTable.css');

    expect(css).toContain('overflow-wrap: break-word');
    expect(css).toContain('word-break: normal');
    expect(css.match(/anywhere/g)?.length).toBe(2);
    expect(css).toMatch(/:has\(\[data-audit-was-becomes\]\)\s*\{[^}]*anywhere/);
    expect(css).toMatch(/\[data-audit-technical\]\s*\{[^}]*anywhere/);
  });

  // ✎ P-1124-4 (живий екран 24.11: заголовок «Changed at» — два рядки, дата — три): на робочому столі заголовки й
  // час — одним рядком; на телефоні — ні (прокрутка сторінки, C1 P3-1). Мутація: прибрати `nowrap` — червоне.
  it('заголовки й час запису — nowrap від 64em, не на телефоні', () => {
    const css = code('features/audit/auditTable.css');

    expect(css).toMatch(
      /@media \(min-width: 64em\)\s*\{\s*\.ecr-audit-table th,\s*\.ecr-audit-table td\[data-audit-time-cell\]\s*\{\s*white-space: nowrap;/,
    );
    expect(css.replace(/@media[^{]*\{[\s\S]*?\}\s*\}/g, '')).not.toContain('nowrap');
  });
});
