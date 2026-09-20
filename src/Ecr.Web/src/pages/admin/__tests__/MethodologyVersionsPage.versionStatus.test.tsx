import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { statusTone } from '@/shared/ui/StatusBadge';
import { testTheme } from '@/test/render';

/**
 * Статус версії методології показувався КОДОМ СЕРВЕРА.
 *
 * ⛔ У переліку стояло `<Badge variant={version.isEditable ? 'light' :
 * 'filled'}>{version.status}</Badge>`. Дві вади в одному рядку:
 *
 *  1. `Draft`/`Published`/`Deprecated` потрапляли на екран англійськими
 *     словами з `Ecr.Domain/Enums` — код сервера не є текстом інтерфейсу й не
 *     перекладається, а продукт тримає три мови (`D-95`);
 *  2. колір ніс `isEditable`, тобто **«чи можна правити», а не статус**.
 *     Застаріла версія і чернетка різнилися не тим, чим вони є.
 *
 * ⚠ Словник тут спільний із `TemplateVersion.Status` — про це прямо сказано в
 * `statusTable.version`, тож `kind="version"` це не здогад.
 *
 * ⛔ У сторінки не було жодного тесту на цей рядок — тому він і прожив стільки.
 */

const versions = [
  {
    id: 1,
    versionNumber: '1.0',
    status: 'Deprecated',
    isEditable: false,
    numericMode: 'Decimal',
    calendarMode: 'Gregorian',
    traceLevel: 'None',
    effectiveFrom: '2026-01-01',
    effectiveTo: null,
    publishedAt: null,
  },
  {
    id: 2,
    versionNumber: '2.0',
    status: 'Draft',
    isEditable: true,
    numericMode: 'Decimal',
    calendarMode: 'Gregorian',
    traceLevel: 'None',
    effectiveFrom: '2026-02-01',
    effectiveTo: null,
    publishedAt: null,
  },
];

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      /*
       * ⛔ Порядок і форма перевірок тут — не стиль, а вимога, і я на цьому
       * уже спіткнувся: перша редакція заглушки починалася з
       * `url.includes('/api/v1/me')`, а `/api/v1/methodologies/5/versions`
       * ЦЕЙ ПІДРЯДОК МІСТИТЬ — `/api/v1/me` є префіксом `/api/v1/methodologies`.
       * Запит версій отримував профіль користувача, `versions.data` ставало
       * об'єктом, і сторінка падала на `versions.find is not a function` —
       * тобто тест червонів не з тієї причини, яку перевіряє.
       *
       * ⚠ Тому `/me` звіряється ТОЧНО (`endsWith`), а вужчі маршрути стоять
       * перед ширшими.
       */
      if (path.endsWith('/api/v1/me')) {
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
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (/\/methodologies\/\d+\/versions$/.test(path)) {
        return new Response(JSON.stringify(versions), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      // ⚠ Решта запитів сторінки (формули, одиниці) для цього твердження
      // байдужі — але форма відповіді має бути масивом, інакше сторінка впаде
      // не з тієї причини, яку перевіряє тест.
      return new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/methodologies/5/versions']}>
        <QueryClientProvider client={client}>
          <Routes>
            {/* ⚠ Ім'я параметра — саме `id`: сторінка читає `params['id']`. */}
            <Route path="/admin/methodologies/:id/versions" element={<MethodologyVersionsPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** ⚠ Сторінка важка в jsdom: сім панелей змісту за `import()`. */
const SlowEnvTimeout = 120_000;

function tone(state: string): string | null {
  return (
    document.querySelector(`[data-status-state="${state}"]`)?.getAttribute('data-status-tone') ??
    null
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyVersionsPage: статус версії — з набору, не кодом сервера', () => {
  it(
    'кожна версія має позначку з розпізнаним станом, а код сервера не видно',
    async () => {
      respond();
      show();

      await screen.findByText('1.0', {}, { timeout: SlowEnvTimeout });

      /*
       * ⛔ `data-status-state` кладе `StatusBadge` — тобто стан пройшов через
       * таблицю набору, а не був надрукований рядком. Мутація «повернути
       * `<Badge>{version.status}</Badge>`» лишає цей перелік порожнім.
       */
      const states = [...document.querySelectorAll('[data-status-state]')].map((node) =>
        node.getAttribute('data-status-state'),
      );

      expect(states).toEqual(['Deprecated', 'Draft']);

      /*
       * ⚠ І підпис: без завантаженого каталогу `t()` чесно повертає позначений
       * ключ, тож голого слова `Deprecated` на екрані бути не може.
       */
      expect(screen.queryAllByText('Deprecated')).toHaveLength(0);
      expect(document.body.textContent ?? '').toContain('status.version.Deprecated');
    },
    SlowEnvTimeout,
  );

  it(
    'застаріла версія відрізняється від чернетки ТОНОМ, а не лише словом',
    async () => {
      /*
       * ⛔ Саме це й було зламано. `variant` бейджа ніс `isEditable`, тобто
       * чернетка і застаріла версія різнилися тим, «чи можна правити», а не
       * тим, чим вони є.
       *
       * ⚠ Тони звіряються з ТИМ САМИМ джерелом, яким малює решта застосунку, а
       * не з літералами `'muted'`/`'neutral'`: літерал лишився б зеленим і
       * тоді, коли сторінка малює власною таблицею, яка випадково збіглася.
       */
      respond();
      show();

      await screen.findByText('1.0', {}, { timeout: SlowEnvTimeout });

      expect(tone('Deprecated')).toBe(statusTone('version', 'Deprecated'));
      expect(tone('Draft')).toBe(statusTone('version', 'Draft'));
      expect(tone('Deprecated')).not.toBe(tone('Draft'));
    },
    SlowEnvTimeout,
  );
});
