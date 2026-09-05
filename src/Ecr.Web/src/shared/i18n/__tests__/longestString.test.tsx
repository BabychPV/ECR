import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { JSX, ReactNode } from 'react';
import { loadCatalog, t } from '@/shared/i18n';
import { DocumentsPage } from '@/pages/DocumentsPage';

/**
 * Розмітка не ламається від довжини тексту (`ФВ-14.30`).
 *
 * ⛔ jsdom не має розкладки: виміряти переповнення тут неможливо, і тест, який
 * стверджував би «не обрізалося», брехав би. Тому перевіряється те, що
 * перевірити МОЖНА і що є справжньою причиною поломки:
 *
 *   1. найдовший рядок каталогу доходить до DOM ЦІЛИМ — його не ріже код;
 *   2. рядки такої довжини в каталозі справді бувають, тобто тест не
 *      перевіряє вигаданий випадок;
 *   3. фіксованих ширин у полів і кнопок немає — це тримає лінт-правило
 *      `ФВ-14.30` в `eslint.config.js`.
 *
 * Візуальна перевірка лишається ручною: `/_kitchen-sink` містить розділ
 * «Довгий рядок» саме для неї.
 */
const seed = readFileSync(
  path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
  'utf8',
);

/** Усі значення каталогу з `09-seed.sql`. */
function catalogValues(): { key: string; value: string }[] {
  const rows = [...seed.matchAll(/\(N'([^']+)',\s*N'[a-z]{2}',\s*N'([^']*)'/g)];

  return rows.map((row) => ({ key: row[1] ?? '', value: row[2] ?? '' }));
}

function Shell({ children }: { children: ReactNode }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter>{children}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('Найдовший рядок каталогу', () => {
  it('у каталозі є рядки, довші за 60 символів', () => {
    const longest = catalogValues().sort((a, b) => b.value.length - a.value.length)[0];

    // ⚠ Калібрування самого тесту: якби каталог складався з коротких слів,
    // усе нижче було б зеленим і не значило б нічого.
    expect(longest?.value.length ?? 0).toBeGreaterThan(60);
  });

  it('ФВ-14.30: доходить до DOM цілим — його не ріже код', async () => {
    const longest = catalogValues().sort((a, b) => b.value.length - a.value.length)[0];
    expect(longest).toBeDefined();

    const strings: Record<string, string> = {};
    // ⚠ Найдовше значення підставляється в КОЖЕН ключ сторінки: інакше тест
    // перевіряв би одну випадкову позначку замість усієї розмітки.
    for (const row of catalogValues()) strings[row.key] = longest!.value;

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        Promise.resolve(
          new Response(
            JSON.stringify(
              String(input).includes('/ui-strings/')
                ? { languageCode: 'en', revision: 1, strings }
                : { items: [], nextCursor: null },
            ),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        ),
      ),
    );

    await loadCatalog('en', 'public');
    expect(t('documents.title')).toBe(longest!.value);

    render(
      <Shell>
        <DocumentsPage />
      </Shell>,
    );

    // ⛔ `findAllByText` збігається ТОЧНО: обрізання трьома крапками в коді
    // (а не в CSS) зробило б цей пошук безрезультатним. Обрізати має браузер
    // через `text-overflow`, зберігаючи повне значення в DOM для читалки.
    const shown = await screen.findAllByText(longest!.value);
    expect(shown.length).toBeGreaterThan(0);
  });
});
