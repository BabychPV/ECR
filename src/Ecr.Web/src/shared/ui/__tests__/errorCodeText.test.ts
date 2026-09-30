import { describe, it, expect, vi, afterEach } from 'vitest';
import { loadCatalog } from '@/shared/i18n';
import { errorCodeText } from '@/shared/ui/problemText';

/**
 * `X-04`: причина провалу фонової задачі — з каталогу за КОДОМ, а не сирий
 * `JobStatus.error` (`ex.Message` сервера: українське речення розробника чи
 * «Violation of PRIMARY KEY…» на англійському екрані).
 *
 * ⚠ Каталог тут завантажується по-справжньому (`loadCatalog` зі
 * замокненим `fetch`): без нього `hasText` завжди `false`, і тест «відомий
 * код → текст каталогу» був би порожнім.
 */

function catalog(strings: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'en', revision: 3, strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"private-en-3"' },
        }),
      ),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('errorCodeText: причина провалу задачі з каталогу', () => {
  it('відомий код — текст каталогу, а не запасний', async () => {
    catalog({ 'err.ECR-PRD-0409': 'The period is closed' });
    await loadCatalog('en', 'private');

    // ⛔ Мутація «повертати завжди fallback» (стара поведінка по суті —
    // жодного звернення до каталогу) робить цей рядок червоним.
    expect(errorCodeText('ECR-PRD-0409', 'Export failed')).toBe('The period is closed');
  });

  it('код без рядка в каталозі — запасний текст, а не ⟦err.…⟧', async () => {
    catalog({ 'err.ECR-PRD-0409': 'The period is closed' });
    await loadCatalog('en', 'private');

    // ⛔ Мутація «`t(key)` без `hasText`» дає тут `⟦err.ECR-JOB-0409⟧`.
    expect(errorCodeText('ECR-JOB-0409', 'Export failed')).toBe('Export failed');
  });

  it('коду немає (прибирання на старті не записало причини) — запасний текст', () => {
    expect(errorCodeText(null, 'Export failed')).toBe('Export failed');
    expect(errorCodeText(undefined, 'Export failed')).toBe('Export failed');
    expect(errorCodeText('', 'Export failed')).toBe('Export failed');
  });
});
