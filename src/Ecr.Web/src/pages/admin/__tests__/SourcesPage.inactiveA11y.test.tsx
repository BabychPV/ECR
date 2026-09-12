import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * Неактивне джерело позначалося ЛИШЕ `opacity={0.5}` на рядку таблиці — суто
 * піксельна ознака. Зчитувач екрана не читає прозорість, тому рядок
 * неактивного джерела звучав так само, як активного (порушення ФВ-14,
 * доступність).
 *
 * ⛔ Мутаційний доказ: тест шукає ДОСТУПНЕ ім'я (`aria-label`/текст бейджа),
 * що несе слово «Inactive» — не просто «рядок існує». Відкіт до голого
 * `opacity={0.5}` без жодного текстового/aria-носія залишає рядок без
 * доступного імені, що містить це слово, і тест падає.
 */
// ⚠ Назви НАВМИСНО не містять слова «Inactive» — інакше збіг зі словом у
// власній назві джерела (а не з доданою позначкою) робив би цей тест
// зеленим і без фіксу: перша версія тесту саме так і помилялась (назва
// `'Inactive source'` сама по собі містила шукане слово).
const sources = [
  { id: 1, code: 'FLD-1', displayName: 'Field weather feed', entityPath: null, isActive: true, lastRun: null, oldestGap: null, transport: 'Rest' },
  { id: 2, code: 'FLD-2', displayName: 'Legacy soil sensor', entityPath: null, isActive: false, lastRun: null, oldestGap: null, transport: 'Rest' },
];

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'Тестовий адміністратор',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/sources')) {
        return new Response(JSON.stringify(sources), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SourcesPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourcesPage: доступна позначка неактивного джерела', () => {
  it('неактивне джерело має текстовий/aria-носій зі словом «Inactive», активне — ні', async () => {
    respond();
    show();

    await screen.findByText('Field weather feed');

    // ⛔ Головне твердження: рядок неактивного джерела має доступне ім'я чи
    // видимий бейдж, що прямо каже «неактивне» — не лише прозорість. Слово
    // НЕ входить у власну назву джерела (див. коментар над фікстурою вище),
    // тому збіг можливий ЛИШЕ через доданий бейдж/aria-label.
    //
    // ⚠ Без завантаженого каталогу перекладів `t('sources.inactive')`
    // повертає позначений ключ `⟦sources.inactive⟧` (`shared/i18n/index.ts`,
    // `Missing`), а не англійський текст «Inactive» — регістронезалежний
    // `/inactive/i` ловить ОБИДВА варіанти (справжній переклад і позначений
    // ключ у тесті), не прив'язуючись до конкретного рядка каталогу.
    const inactiveRow = (await screen.findByText('Legacy soil sensor')).closest('tr');
    expect(inactiveRow).not.toBeNull();
    expect(inactiveRow?.textContent ?? '').toMatch(/inactive/i);
    expect(inactiveRow?.getAttribute('aria-label') ?? '').toMatch(/inactive/i);

    const activeRow = (await screen.findByText('Field weather feed')).closest('tr');
    expect(activeRow?.getAttribute('aria-label')).toBeNull();
    expect(activeRow?.textContent ?? '').not.toMatch(/inactive/i);
  });
});
