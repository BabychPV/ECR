import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { formatDate, formatDateTime } from '@/shared/format';
import { testTheme } from '@/test/render';

/**
 * `UI-07`: початок прогалини читабельний на екрані, точний — у розмітці.
 *
 * ⛔ Навіщо окремо від `shared/ui/__tests__/Timestamp.test.tsx`. Той доводить
 * поведінку КОМПОНЕНТА і лишиться зеленим, якщо цю сторінку не змінити
 * взагалі. Тут інше твердження: саме колонка «прогалина» показує момент через
 * набір, а не друкує сирий ISO у бейдж.
 *
 * ⚠ Обидві половини обов'язкові й поодинці порожні: сама лише рівність із
 * `formatDateTime(...)` лишилася б зеленою на форматувальнику, що повертає
 * вхід; саме лише «текст не дорівнює входу» — на чому завгодно, що зіпсувало
 * значення.
 *
 * ⚠ Очікуваний текст береться з `shared/format`, а не пишеться літералом:
 * літерал перевіряв би версію ICU у Node і червонів би від оновлення
 * середовища, нічого не зламавши.
 */

const Gap = '2026-09-04T02:17:43.271Z';

const Session = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

/*
 * ⚠ Два джерела, а не одне: друге — з суцільним покриттям (`oldestGap: null`).
 * Без нього тест не відрізнив би «момент пройшов через набір» від «через набір
 * пройшла ВСЯ колонка, включно з порожньою».
 */
const sources = [
  {
    id: 1,
    code: 'FLD-1',
    displayName: 'Field weather feed',
    entityPath: null,
    isActive: true,
    lastRun: null,
    oldestGap: Gap,
    transport: 'Rest',
  },
  {
    id: 2,
    code: 'FLD-2',
    displayName: 'Stack analyzer',
    entityPath: null,
    isActive: true,
    lastRun: null,
    oldestGap: null,
    transport: 'Rest',
  },
];

/**
 * Заглушка переліку джерел.
 *
 * ⚠ `SourcesPage` читає `apiFetch<SourceEntityStatus[]>` — МАСИВ, не
 * `{ items: [...] }`. Помилка тут не дає читабельної відмови: сторінка падає
 * на `all.length`, таблиці не з'являється, і тест помирає за таймаутом
 * «зовсім не з тієї причини».
 *
 * ⚠ `/api/v1/me` матчиться `endsWith`, а не `includes`: підрядок `/api/v1/me`
 * містять і `/api/v1/methodologies`, і `/api/v1/me...` — будь-яка з таких
 * адрес отримала б профіль замість своїх даних.
 */
function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost').pathname;

      if (url.endsWith('/api/v1/me')) {
        return new Response(JSON.stringify(Session), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.endsWith('/api/v1/data-sources')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.endsWith('/api/v1/sources')) {
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
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <SourcesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('SourcesPage: початок найстарішої прогалини', () => {
  it(
    'читабельний на екрані, точний у розмітці',
    async () => {
      respond();
      show();

      await screen.findByText('Field weather feed', {}, { timeout: SlowEnvTimeout });

      const node = document.querySelector(`time[datetime="${Gap}"]`);

      expect(node, 'момент не пройшов через набір').not.toBeNull();

      // ⛔ Друга половина: точне значення НЕ загубилося — воно те саме, що
      // віддав сервер, і його видно в DOM без наведення.
      expect(node?.getAttribute('datetime')).toBe(Gap);
      expect(node?.getAttribute('title')).toBe(Gap);

      // ⛔ Перша половина: око більше не бачить сирого ISO.
      expect(node?.textContent).toBe(formatDateTime(Gap));
      expect(node?.textContent).not.toBe(Gap);
      expect(node?.textContent).not.toMatch(/T\d{2}:\d{2}/);
    },
    SlowEnvTimeout,
  );

  it(
    'із годиною, але без секунд',
    async () => {
      respond();
      show();

      await screen.findByText('Field weather feed', {}, { timeout: SlowEnvTimeout });

      const shown = document.querySelector(`time[datetime="${Gap}"]`)?.textContent ?? '';

      /*
       * ⛔ Година ПОТРІБНА. Клітинка відповідає на «з якого моменту даних
       * немає», і дія за цією відповіддю — збір за вікно. Прогалина шукається
       * за 45 днів назад, тобто найсвіжіша починається сьогодні: без години
       * вона читалася б як «увесь день порожній». Це не `PeriodsPage`, де
       * межа відповідає на «який це місяць».
       */
      expect(shown, 'година зникла — клітинка стала `dateOnly`').toMatch(/\d{1,2}:\d{2}/);
      expect(shown).not.toBe(formatDate(Gap));

      /*
       * ⛔ А секунди НЕ потрібні, хоч сусідня «прогалина» в `MappingGaps` їх
       * має. Там КОЛОНКА сусідніх міток телеметрії, що різняться між собою
       * секундою; тут одне значення на джерело, і питання міряється годинами
       * й днями. Мітка має `.271` — якби `precise` увімкнули, секунди були б
       * видні, і цей рядок почервонів би.
       */
      expect(shown, 'секунди на екрані — це увімкнений `precise`').not.toMatch(/\d{1,2}:\d{2}:\d{2}/);
    },
    SlowEnvTimeout,
  );

  it(
    'суцільне покриття лишається прочерком, а не проходить через набір',
    async () => {
      respond();
      show();

      await screen.findByText('Stack analyzer', {}, { timeout: SlowEnvTimeout });

      /*
       * ⛔ `oldestGap: null` — це ДОБРА новина, і власна гілка під неї
       * лишилася навмисно: `fallback` самого `Timestamp` намалював би тире
       * всередині того самого червоного бейджа, яким позначена прогалина,
       * тобто збрехав би кольором. Рівно один вузол набору на сторінці і
       * доводить, що друге джерело крізь нього не пішло.
       */
      expect(document.querySelectorAll('[data-timestamp]')).toHaveLength(1);
      expect(screen.getByText('—')).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});
