import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';

/**
 * Q-287: «Архівувати» на цій сторінці спрацьовувало ОДРАЗУ по кліку — на
 * відміну від «Відкрити»/«Зробити поточним» на цій самій сторінці, які
 * вимагають підтвердження (`ReasonModal`). Дія незворотна (`Archived` —
 * кінцевий стан, ФВ-1.15), тому випадковий клік коштує так само дорого, як і
 * випадкове відкриття закритого періоду.
 *
 * ⛔ Мутаційний доказ: тест перевіряє не «є діалог» (косметика), а що клік по
 * кнопці «Архівувати» САМ ПО СОБІ (без підтвердження) НЕ надсилає запит і не
 * змінює стан — рівно те, що ламалося до фіксу. Повернення `onClick={() =>
 * archive.mutate(...)}` напряму на кнопці зробить цей тест червоним: перший
 * клік одразу викличе `POST .../archive`. Перевірено вручну перед комітом
 * (RED на відкоченому коді, GREEN на виправленому).
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
  periods: [],
};

function respond(): { calls: string[]; archiveCalls: number } {
  const state = { calls: [] as string[], archiveCalls: 0 };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      state.calls.push(`${init?.method ?? 'GET'} ${url}`);

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
            userName: 'Тестовий адміністратор',
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

      if (url.includes('/api/v1/projects') && url.endsWith('/archive')) {
        state.archiveCalls += 1;

        return new Response(JSON.stringify(null), { status: 200 });
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

  return state;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
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

// ⚠ Середовище прогону — повільне (той самий ефект, що й у `Q-274`/`Q-275`:
// `create-project-modal.render.test.tsx` таймаутить на 400_000ms навіть у
// ПОВНІЙ ізоляції, без жодних сусідніх процесів). 30с виявилось замало.
const SlowEnvTimeout = 400_000;

describe('PeriodsPage: підтвердження архівації (Q-287)', () => {
  it(
    'клік «Архівувати» БЕЗ підтвердження не надсилає запит',
    async () => {
      const state = respond();
      const user = userEvent.setup();
      show();

      const archiveButton = await screen.findByRole(
        'button',
        { name: /archive/i },
        { timeout: SlowEnvTimeout },
      );
      await user.click(archiveButton);

      // ⛔ Головне твердження. До фіксу цей клік ОДРАЗУ викликав `POST
      // .../archive` — тут його не має статись, доки не підтверджено в діалозі.
      expect(state.archiveCalls).toBe(0);

      // Натомість повинен з'явитися діалог підтвердження з кнопкою «Скасувати».
      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      expect(within(dialog).getByRole('button', { name: /cancel/i })).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'підтвердження в діалозі надсилає запит рівно один раз',
    async () => {
      const state = respond();
      const user = userEvent.setup();
      show();

      const archiveButton = await screen.findByRole(
        'button',
        { name: /archive/i },
        { timeout: SlowEnvTimeout },
      );
      await user.click(archiveButton);

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      const confirmButtons = within(dialog).getAllByRole('button', { name: /archive/i });
      await user.click(confirmButtons[confirmButtons.length - 1]!);

      // ⛔ Рівно ОДИН запит на підтвердження — не нуль (як у тесті вище) і не
      // більше одного (подвійний клік / повторний рендер кнопки).
      await waitFor(() => expect(state.archiveCalls).toBe(1), { timeout: SlowEnvTimeout });

      // Діалог закривається після успіху (`setArchiving(false)` в onSuccess).
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull(), {
        timeout: SlowEnvTimeout,
      });
    },
    SlowEnvTimeout,
  );
});
