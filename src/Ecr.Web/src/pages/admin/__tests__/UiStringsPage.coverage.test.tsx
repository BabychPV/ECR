import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { UiStringsPage } from '../UiStringsPage';
import { testTheme } from '@/test/render';

/**
 * `BE-13`: лічильники покриття над таблицею і перемикач «лише відсутні».
 *
 * ⚠ Каталог тут зібрано так, щоб клієнтський здогад «значення збігається з
 * оригіналом → не перекладено» ПОМИЛЯВСЯ: `c.ok` російською теж «OK», і це
 * справжній переклад. Відсутній насправді лише `b.key`, і знає про це тільки
 * сервер.
 */

const SlowEnvTimeout = 400_000;

const English = { 'a.key': 'Alpha', 'b.key': 'Beta', 'c.ok': 'OK' };
const Russian = { 'a.key': 'Альфа', 'b.key': 'Beta', 'c.ok': 'OK' };

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);

    if (url.includes('/ui-strings/coverage')) {
      return json({
        languages: [
          { languageCode: 'en', total: 3, translated: 3, missing: 0 },
          { languageCode: 'ru', total: 3, translated: 2, missing: 1 },
        ],
      });
    }

    if (url.includes('/ui-strings?')) {
      return json({
        languageCode: 'ru',
        items: [{ key: 'b.key', reference: 'Beta', value: null }],
      });
    }

    if (url.includes('/ui-strings/ru')) {
      return json({ languageCode: 'ru', revision: 1, strings: Russian });
    }

    if (url.includes('/ui-strings/')) {
      return json({ languageCode: 'en', revision: 1, strings: English });
    }

    if (url.includes('/api/v1/languages')) {
      return json([
        { code: 'en', nameNative: 'English' },
        { code: 'ru', nameNative: 'Русский' },
      ]);
    }

    throw new Error(`неочікуваний запит у тесті: ${url}`);
  });

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/ui-strings?lang=ru']}>
          <UiStringsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UiStringsPage: покриття перекладу і «лише відсутні»', () => {
  it(
    'над таблицею — лічильник на кожну мову, з числами сервера',
    async () => {
      mockFetch();
      show();

      const coverage = await screen.findByTestId('ui-strings-coverage', {}, { timeout: SlowEnvTimeout });

      // ⚠ Каталог інтерфейсу в тесті не піднімається, тож `t()` віддає
      // позначений ключ РАЗОМ із параметрами — їх і читаємо.
      expect(coverage.children).toHaveLength(2);
      expect(coverage.textContent).toContain('language=ru, translated=2, total=3, missing=1');
    },
    SlowEnvTimeout,
  );

  it(
    '«лише відсутні» питає сервер і лишає рівно те, що він назвав',
    async () => {
      const fetchMock = mockFetch();
      show();

      await screen.findByText('a.key', {}, { timeout: SlowEnvTimeout });
      expect(screen.getByText('c.ok')).toBeTruthy();

      fireEvent.click(screen.getByLabelText(/uiStrings\.missingOnly/));

      // ⚠ Між кліком і відповіддю сервера таблиці немає зовсім (скелет), тож
      // чекаємо саме на появу рядка, а не на зникнення сусіднього.
      await screen.findByText('b.key', {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByText('a.key')).toBeNull();

      // ⛔ Мутаційний доказ: заміни `missingKeys.has(key)` на клієнтський
      // здогад `strings[key] === original[key]` — `c.ok` лишиться в таблиці.
      expect(screen.queryByText('c.ok')).toBeNull();
      expect(screen.getByTestId('ui-strings-count').textContent).toBe('1 / 1');

      const asked = fetchMock.mock.calls.map(([input]) => String(input));
      expect(asked.some((url) => url.includes('/api/v1/ui-strings?lang=ru&missingOnly=true'))).toBe(true);
    },
    SlowEnvTimeout,
  );
});
