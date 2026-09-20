import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { UiStringsPage } from '../UiStringsPage';
import { testTheme } from '@/test/render';

/**
 * Директива D15 §0, правило L10 — той самий клас, що #444/#446/#449/#451/#452,
 * #454/#455/#456.
 *
 * ⛔ Перелік мов збирався через `?? []`, тож відмова `GET /api/v1/languages`
 * давала порожній `Select` — «мов у системі немає». На екрані, де саме
 * ПЕРЕКЛАДАЮТЬ, це найгірше з можливих тверджень: людина не може обрати мову
 * призначення і не знає чому.
 *
 * ⚠ Решта сторінки цим НЕ страждає, і це важливо для меж картки: таблицю
 * тримає `AsyncBoundary`, який уже розбирає `catalog.error ?? reference.error
 * ?? (onlyMissing ? missing.error : null)` — тобто відмова каталогу, еталона
 * чи переліку відсутніх уже показується причиною, а не порожньою таблицею.
 */

const SlowEnvTimeout = 400_000;

const English = { 'a.key': 'Alpha', 'b.key': 'Beta' };
const Russian = { 'a.key': 'Альфа', 'b.key': 'Beta' };

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік мов прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-lang-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockFetch(languagesFail: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/languages')) {
        return languagesFail
          ? json(Refusal, 500)
          : json([
              { code: 'en', nameNative: 'English' },
              { code: 'ru', nameNative: 'Русский' },
            ]);
      }

      if (url.includes('/ui-strings/coverage')) {
        return json({ languages: [{ languageCode: 'ru', total: 2, translated: 1, missing: 1 }] });
      }

      if (url.includes('/ui-strings?')) {
        return json({ languageCode: 'ru', items: [{ key: 'b.key', reference: 'Beta', value: null }] });
      }

      if (url.includes('/ui-strings/ru')) {
        return json({ languageCode: 'ru', revision: 1, strings: Russian });
      }

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: English });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
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

describe('UiStringsPage: відмова переліку мов ≠ «мов немає»', () => {
  it(
    'мови не приїхали — причина з кодом, порожнього переліку НЕМАЄ, а редактор лишається робочим',
    async () => {
      mockFetch(true);
      show();

      /*
       * ⚠ Стеля тут НЕ `SlowEnvTimeout`: банер або є одразу після відмови, або
       * його немає зовсім. З 400 с мутація «прибрати банер» чекала повні
       * 6.6 хвилини, перш ніж почервоніти, — перевірка, яка так довго падає,
       * коштує дорожче, ніж допомагає.
       */
      const alert = await waitFor(() => screen.getByRole('alert'), { timeout: 30_000 });

      expect(alert.textContent ?? '').toContain('перелік мов прочитати не вдалося');
      expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
      expect(screen.queryByRole('textbox', { name: /uiStrings\.language/ })).toBeNull();

      /*
       * ⚠ Твердження про редактор — ПІСЛЯ приходу відмови: інакше воно було б
       * зеленим і на коді, який ховає сторінку цілком (урок із #446). Мова
       * береться з адреси, тож без переліку сторінка й далі перекладає `ru`.
       */
      expect(await screen.findByText('Альфа', {}, { timeout: SlowEnvTimeout })).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'мови приїхали — перелік на місці й доступний, банера немає',
    async () => {
      mockFetch(false);
      show();

      // ⚠ Дочекатися саме ДОСТУПНОГО поля: доки перелік у дорозі, воно
      // навмисно заблоковане.
      await waitFor(
        () => {
          const select = screen.getByRole('textbox', { name: /uiStrings\.language/ });
          expect((select as HTMLInputElement).disabled).toBe(false);
        },
        { timeout: SlowEnvTimeout },
      );

      expect(screen.queryByRole('alert')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
