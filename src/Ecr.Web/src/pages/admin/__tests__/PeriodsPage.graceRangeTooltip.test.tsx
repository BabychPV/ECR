import type { JSX, ReactNode } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 2 (Q-337): «Grace until»/«Range» на сторінці Periods не
 * мали ЖОДНОГО пояснення. Ярлик політики `+15/45` (форма створення проєкту)
 * показує лише два з чотирьох чисел і взагалі не на цій сторінці; «Grace
 * until» (2026-02-01 для періоду 202601) виглядав як просто межа наступного
 * календарного періоду, а не «кінець періоду + пільговий строк».
 *
 * ⛔ Корінь окремої знахідки в тому ж лейні: `RecomputeBoundaries`
 * (`Period.cs`) рахував `ComputedGraceAt` як `PeriodEnd + 1` буквально —
 * `GraceOffsetDays` політики НЕ читався взагалі. Виправлено окремо
 * (`PeriodStateCalculatorTests.GraceOffsetDays_із_політики_реально_зсуває_
 * перехід_Open_у_Grace`); цей тест перевіряє КОМУНІКАЦІЙНУ частину — що
 * тултипи на заголовках пояснюють похідну формулу в термінах РЕАЛЬНИХ
 * чотирьох чисел активної політики проєкту (`calendar.policy`), а не
 * ярлика.
 *
 * ⚠ Mantine `Tooltip` під jsdom не показує вміст на `hover` без
 * floating-ui/portal-позиціонування (реального layout тут немає) —
 * той самий клас відтворюваних обмежень jsdom, що вже задокументований
 * для `MultiSelect`/`Select` в інших тестах цього репозиторію. Підмінено
 * легким заглушником, що рендерить `label` ЗАВЖДИ, поруч із дітьми, без
 * hover і без порталу — і саме тому може стверджувати РЕАЛЬНИЙ текст,
 * підставлений `t()`, а не факт «якийсь тултип десь існує».
 *
 * ✎ Заглушки `Select`/`MultiSelect` по всьому репозиторію знято: їхня
 * причина («Mantine зависає під jsdom») виявилася хибною — насправді це
 * взаємна рекурсія jsdom ↔ nwsapi на станових псевдокласах, і вона вже
 * обірвана (коментар у `src/test/setup.ts`). Ця заглушка ЛИШАЄТЬСЯ, бо
 * причина в неї інша. Спробувано справжній `Tooltip` із наведенням
 * (`fireEvent.mouseOver` на заголовку → `findByRole('tooltip')`): тултип не
 * відкрився взагалі, запит чекав до власного ліміту. Питання не в
 * швидкодії, а в тому, ЯК під jsdom відкрити тултип; поки відповіді немає,
 * заглушка чесніша за послаблене твердження.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  function StubTooltip(props: { label?: ReactNode; children?: ReactNode }): JSX.Element {
    return (
      <span>
        {props.children}
        <span data-testid="tooltip-label">{props.label}</span>
      </span>
    );
  }

  return { ...actual, Tooltip: StubTooltip };
});

const project = {
  id: 7,
  code: 'PRJ-7',
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
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    {
      id: 1,
      periodKey: 202601,
      sequence: 1,
      startsAt: '2026-01-01',
      endsAt: '2026-03-17',
      state: 'Open',
      graceEndsAt: '2026-02-15',
      reopenedUntil: null,
      isCurrent: true,
    },
  ],
};

function mockFetch(): void {
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
            permissions: ['Project.Manage'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects') && url.includes('/periods')) {
        return new Response(JSON.stringify(calendar), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
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
      <MemoryRouter initialEntries={['/admin/periods?projectId=7']}>
        <QueryClientProvider client={client}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('PeriodsPage: тултипи Range/Grace until пояснюють похідну формулу (Q-337, lane2)', () => {
  it(
    'тултип "Range" називає Open offset і Hard-close offset із РЕАЛЬНИМИ числами політики',
    async () => {
      mockFetch();
      show();

      const labels = await screen.findAllByTestId('tooltip-label', {}, { timeout: SlowEnvTimeout });
      const text = labels.map((el) => el.textContent ?? '').join(' | ');

      // ⚠ Каталог перекладів тут НЕ завантажується (той самий підхід, що й
      // інші `PeriodsPage.*.test.tsx` цього репозиторію) — `t()` повертає
      // позначений ключ із підставленими параметрами (`⟦ключ (param=val)⟧`).
      // Це й потрібно: доказ саме в ЧИСЛАХ, підставлених із `calendar.policy`
      // (`open=0`, `hardClose=45`, `code=ECR-Standard`) — а не в статичному
      // англійському тексті самого рядка (`09-seed.sql`).
      //
      // ⛔ Мутаційний доказ: до фіксу заголовок — голий текст без `Tooltip`
      // узагалі, і жоден тултип не міг би містити параметр `hardClose=45`.
      expect(text).toContain('periods.rangeHint');
      expect(text).toContain('open=0');
      expect(text).toContain('hardClose=45');
      expect(text).toContain('code=ECR-Standard');
    },
    SlowEnvTimeout,
  );

  it(
    'тултип "Grace until" називає Grace offset — а не ярлик +15/45 — із РЕАЛЬНИМ числом 15',
    async () => {
      mockFetch();
      show();

      const labels = await screen.findAllByTestId('tooltip-label', {}, { timeout: SlowEnvTimeout });
      const text = labels.map((el) => el.textContent ?? '').join(' | ');

      expect(text).toContain('periods.graceHint');
      expect(text).toContain('grace=15');
      expect(text).toContain('hardClose=45');
      expect(text).toContain('code=ECR-Standard');
    },
    SlowEnvTimeout,
  );
});
