import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { formatDate, formatDateTime } from '@/shared/format';
import { testTheme } from '@/test/render';

/**
 * `UI-07`: моменти на адмінських екранах читабельні, а точні — в розмітці.
 *
 * ⛔ Навіщо цей набір окремо від `shared/ui/__tests__/Timestamp.test.tsx`. Той
 * доводить поведінку КОМПОНЕНТА і лишиться зеленим, якщо жодна сторінка його
 * не викличе. Рівно в цьому стані модуль `shared/format` і прожив увесь час:
 * написаний, протестований, і **жодного продуктового споживача** — сімнадцять
 * місць друкували сирий ISO просто в таблицю. Тому тут перевіряється інше
 * твердження: конкретний екран показує момент через набір.
 *
 * ⚠ Обидві половини обов'язкові, і поодинці кожна порожня:
 *   • сама лише рівність із `formatDateTime(...)` лишилася б зеленою на
 *     компоненті, що нічого не форматує (якби `formatDateTime` повертав вхід);
 *   • сама лише «текст не дорівнює входу» лишилася б зеленою на чому завгодно,
 *     що зіпсувало значення.
 *
 * ⚠ Очікуваний текст береться з `shared/format`, а НЕ пишеться літералом:
 * літерал був би перевіркою версії ICU у Node і червонів би від оновлення
 * середовища, нічого не зламавши.
 */

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

/** Заглушка: одна відповідь за підрядком адреси, решта — `null`. */
function respond(routes: readonly (readonly [string, unknown])[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(Session), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      for (const [needle, body] of routes) {
        if (url.includes(needle)) {
          return new Response(JSON.stringify(body), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(node: React.ReactElement, url: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={client}>{node}</QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Вузол `<time>` із точно цим `dateTime`. */
function timeNode(iso: string): HTMLElement | null {
  return document.querySelector(`time[datetime="${iso}"]`);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('AuditPage: момент правки', () => {
  const ChangedAt = '2026-01-05T10:00:00Z';

  it(
    'читабельний на екрані, точний у розмітці',
    async () => {
      respond([
        [
          '/api/v1/audit/cells',
          {
            items: [
              {
                changedAt: ChangedAt,
                changedByUserId: 41,
                columnDefId: 11,
                documentId: 7,
                isLateEdit: false,
                newValue: '2',
                oldValue: '1',
                origin: 'Import',
                periodKey: 202601,
                rowKey: 'R1',
              },
            ],
            nextCursor: null,
            totalCount: null,
          },
        ],
      ]);
      show(<AuditPage />, '/admin/audit');

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      const node = timeNode(ChangedAt);

      /*
       * ⛔ Саме тут аргумент «журнал — доказ, тож сирий ISO» і знімається:
       * точне значення нікуди не діло́ся — воно в `dateTime`. Змінилося лише
       * те, що бачить око.
       */
      expect(node, 'момент не пройшов через набір').not.toBeNull();
      expect(node?.textContent).toBe(formatDateTime(ChangedAt));
      expect(node?.textContent).not.toBe(ChangedAt);
      expect(node?.textContent).not.toMatch(/T\d{2}:\d{2}/);
    },
    SlowEnvTimeout,
  );
});

describe('PeriodsPage: межі періоду і крайні строки', () => {
  const StartsAt = '2026-01-01';
  const GraceEndsAt = '2026-02-15T18:00:00Z';

  const project = {
    id: 7,
    code: 'P7',
    nameL10n: { values: { en: 'Проєкт' } },
    status: 'Active' as const,
    periodKind: 'Monthly' as const,
    periodCount: 1,
    currentPeriodId: null,
    timeZoneId: 'Europe/Kyiv',
  };

  const calendar = {
    projectId: 7,
    periodKind: 'Monthly' as const,
    currentPeriodMode: 'Auto' as const,
    timeZoneId: 'Europe/Kyiv',
    policy: {
      id: 1,
      code: 'ECR-Standard',
      openOffsetDays: 0,
      graceOffsetDays: 15,
      hardCloseOffsetDays: 45,
      yearGraceOffsetDays: 45,
    },
    periods: [
      {
        id: 1,
        periodKey: 202601,
        sequence: 1,
        startsAt: StartsAt,
        endsAt: '2026-02-01',
        state: 'Open',
        graceEndsAt: GraceEndsAt,
        reopenedUntil: null,
        isCurrent: true,
      },
    ],
  };

  it(
    'межа періоду — без години, пільговий строк — із нею',
    async () => {
      respond([
        ['/periods', calendar],
        ['/api/v1/projects', { items: [project], nextCursor: null, totalCount: 1 }],
      ]);
      show(<PeriodsPage />, '/admin/periods?projectId=7');

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      /*
       * ⛔ Обидві половини в одному тесті навмисно: предмет — саме РІЗНИЦЯ.
       * Межа періоду відповідає на питання «який це місяць», тож година в ній
       * зайва; пільговий строк — це КРАЙНІЙ СТРОК, і без години він не
       * відповідає на питання «чи встигну ще сьогодні».
       */
      const start = timeNode(StartsAt);
      expect(start, 'межу періоду не намальовано елементом <time>').not.toBeNull();
      expect(start?.textContent).toBe(formatDate(StartsAt));
      expect(start?.textContent).not.toMatch(/\d{1,2}:\d{2}/);

      const grace = timeNode(GraceEndsAt);
      expect(grace, 'пільговий строк не намальовано елементом <time>').not.toBeNull();
      expect(grace?.textContent).toBe(formatDateTime(GraceEndsAt));
      expect(grace?.textContent).toMatch(/\d{1,2}:\d{2}/);
    },
    SlowEnvTimeout,
  );
});
