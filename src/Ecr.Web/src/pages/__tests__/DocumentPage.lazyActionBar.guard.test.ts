import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * ✎ debt-1110: сторож запасу бюджету маршруту `DocumentPage` (D-132). Маршрут стояв на 249.6 з 250 КБ gzip; панель
 * дій аркуша (`DocumentActionBar` → модель `SheetActions`) пішла за `import()` — 243.2 КБ. Статичний імпорт будь-якого
 * з двох модулів повернув би ~6 КБ у чанк сторінки, і гейт бюджету побачив би це лише тоді, коли запас уже з'їдено.
 *
 * Мутація (локально): повернути `import { DocumentActionBar } from '@/features/documents/DocumentActionBar'` —
 * червоне.
 */
const Page = path.resolve(process.cwd(), 'src', 'pages', 'DocumentPage.tsx');

/** Код без коментарів: згадка в поясненні — не імпорт. */
function codeOf(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');
}

describe('DocumentPage: панель дій — лише лінивим чанком', () => {
  const code = codeOf(readFileSync(Page, 'utf8'));

  it('немає статичного імпорту DocumentActionBar чи SheetActions', () => {
    expect(code).not.toMatch(/^import\s[^;]*from\s+'@\/features\/documents\/DocumentActionBar'/m);
    expect(code).not.toMatch(/^import\s[^;]*from\s+'@\/features\/workflow\/SheetActions'/m);
  });

  it('панель вантажиться через import()', () => {
    expect(code).toMatch(/import\('@\/features\/documents\/DocumentActionBar'\)/);
  });
});
