import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Діалог доступу користувача (з розрізом effective-access, `ФВ-6.16`) — ЛІНИВИЙ чанк (`D-132`):
 * закриття повертає фокус на кнопку «Access», що його відкрила (WCAG 2.4.3).
 *
 * ⛔ Mantine запам'ятовує відкривача лише коли `opened` ЗМІНЮЄТЬСЯ (`useFocusReturn` → `useDidUpdate`),
 * а лінивий діалог при ПЕРШОМУ відкритті монтується вже відкритим — і Escape лишав фокус на `body`.
 * Тому перевіряються обидва відкриття: перше (монтування) і друге (той самий змонтований діалог).
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): без `accessFocus.remember()`/`restore()` у `SecurityPage`
 * перше відкриття червоне — фокус після Escape на `body`.
 */
const Strings: Record<string, string> = {
  'security.users': 'Users',
  'security.access': 'Access',
  'common.cancel': 'Cancel',
  'groupRoles.validFrom': 'Valid from',
};

const Users = {
  items: [
    {
      id: 1,
      userName: 'jdoe',
      displayName: 'Jane Doe',
      provider: 'Local',
      email: null,
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
      roleIds: [],
    },
  ],
  nextCursor: null,
  totalCount: 1,
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (/\/users(\?|$)/.test(url)) return json(Users);
      if (/\/api\/v1\/me(\?|$)/.test(url)) {
        return json({
          userId: 0,
          userName: 'admin',
          language: 'en',
          permissions: ['Security.ManageUsers', 'Security.ManageRoles'],
          isSimulation: false,
        });
      }

      return json([]);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SecurityPage: лінивий діалог доступу повертає фокус відкривачу', () => {
  it('перше й друге відкриття — Escape повертає фокус на «Access»', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    // ⛔ job `shuffle` (run 42776777315, seed 23107): воркер vitest вісім хвилин крутився на цьому файлі й помер
    // `Worker exited … exit code 1` без жодного впалого тесту. Причина — не продукт, а `act()`: Enter тут
    // ішов, поки ліниві чанки СТОРІНКИ ще доїжджали (вкладка «Users» — `GroupAssignmentsPanel` з лінивим
    // `DateInput`, обгортки «New role»/«New user»). Під навантаженням (CI; локально — 12 паралельних прогонів,
    // 2–4 з 12 висли) React у синхронному `flushActQueue` нескінченно перезапускав рендери меж `Suspense`,
    // чиї `import()` не можуть завершитися, доки цикл не віддасть керування; черга `act` росла мільйонами
    // завдань до вичерпання купи. Тому чанки сторінки вантажимо ДО рендера, а нижче чекаємо, доки найглибший
    // із них (`DateInput` у панелі груп) уже на екрані. Сам діалог доступу наперед НЕ вантажиться: його перше
    // відкриття і далі монтує лінивий чанк уже відкритим — саме це тут і перевіряється.
    await Promise.all([
      import('@/features/security/GroupAssignmentsPanel'),
      import('@/features/security/CreateRoleModal'),
      import('@/features/security/CreateUserModal'),
      import('@/shared/dates/DateInputWithStyles'),
    ]);

    const user = userEvent.setup();
    render(
      <MantineProvider theme={withTestDefaults(theme)}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter initialEntries={['/admin/security?tab=users']}>
            <SecurityPage />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    const opener = await screen.findByRole('button', { name: 'Access' });
    await screen.findByLabelText('Valid from');

    for (const attempt of ['перше', 'друге']) {
      opener.focus();
      await user.keyboard('{Enter}');

      const dialog = await screen.findByRole('dialog');
      await waitFor(() => expect(dialog.contains(document.activeElement), `${attempt}: фокус у діалозі`).toBe(true));

      await user.keyboard('{Escape}');

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement, `${attempt}: фокус на відкривачі`).toBe(opener));
    }
  }, 60_000);
});
