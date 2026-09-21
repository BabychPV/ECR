import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
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
 * підказки на заголовках пояснюють похідну формулу в термінах РЕАЛЬНИХ
 * чотирьох чисел активної політики проєкту (`calendar.policy`), а не
 * ярлика.
 *
 * ✎ Заголовки переведено з `Tooltip` на `Hint`: `Tooltip` відкривався лише
 * наведенням, а сам заголовок — текст поза порядком табуляції, тож з
 * клавіатури формулу не було видно НІКОЛИ. Заглушку `Tooltip` знято — `Hint`
 * відкривається під jsdom справжнім фокусом, і тест тепер доводить саме
 * доступність: опис є без наведення, заголовок у порядку табуляції, фокус
 * відкриває `role="tooltip"`.
 */

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

/** Текст заголовка колонки (`periods.range`/`periods.grace`) — сам тригер. */
async function header(key: string): Promise<HTMLElement> {
  // ⚠ `(?!Hint)`: прихований вузол опису лежить у тій самій клітинці й
  // містить `periods.rangeHint`, а `getByText` прихованих не пропускає.
  const pattern = new RegExp(`${key.replace('.', '\\.')}(?!Hint)`);
  const th = await screen.findByRole('columnheader', { name: pattern }, { timeout: SlowEnvTimeout });

  return within(th).getByText(pattern);
}

/**
 * Опис тригера з `aria-describedby` — те, що озвучить читач при фокусі.
 * ⚠ Каталог перекладів тут НЕ завантажується: `t()` повертає позначений
 * ключ із підставленими параметрами (`⟦ключ (param=val)⟧`). Доказ — саме в
 * ЧИСЛАХ із `calendar.policy`, а не в статичному тексті рядка (`09-seed.sql`).
 */
function description(el: HTMLElement): string {
  const ids = (el.getAttribute('aria-describedby') ?? '').split(' ').filter(Boolean);

  return ids.map((id) => document.getElementById(id)?.textContent ?? '').join(' ');
}

/**
 * ⛔ Мутаційний доказ: на `Tooltip` (до переводу) заголовок не мав ні
 * `tabindex`, ні `aria-describedby`, і фокус не відкривав нічого — усі три
 * твердження нижче червоні.
 */
async function expectKeyboardHint(trigger: HTMLElement, key: string): Promise<string> {
  // У порядку табуляції: заголовок — `<span>`, сам по собі не фокусується.
  expect(trigger.tabIndex).toBe(0);

  // Опис прив'язаний ще ДО будь-якої взаємодії.
  const text = description(trigger);
  expect(text).toContain(key);

  // Фокус (не наведення) відкриває видиму підказку.
  trigger.focus();
  expect((await screen.findByRole('tooltip')).textContent).toContain(key);

  return text;
}

describe('PeriodsPage: підказки Range/Grace until пояснюють похідну формулу й доступні з клавіатури (Q-337, lane2)', () => {
  it(
    'підказка "Range" називає Open offset і Hard-close offset із РЕАЛЬНИМИ числами політики',
    async () => {
      mockFetch();
      show();

      const text = await expectKeyboardHint(await header('periods.range'), 'periods.rangeHint');

      expect(text).toContain('open=0');
      expect(text).toContain('hardClose=45');
      expect(text).toContain('code=ECR-Standard');
    },
    SlowEnvTimeout,
  );

  it(
    'підказка "Grace until" називає Grace offset — а не ярлик +15/45 — із РЕАЛЬНИМ числом 15',
    async () => {
      mockFetch();
      show();

      const text = await expectKeyboardHint(await header('periods.grace'), 'periods.graceHint');

      expect(text).toContain('grace=15');
      expect(text).toContain('hardClose=45');
      expect(text).toContain('code=ECR-Standard');
    },
    SlowEnvTimeout,
  );
});
