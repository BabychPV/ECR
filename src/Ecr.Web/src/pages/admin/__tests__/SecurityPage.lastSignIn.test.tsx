import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { formatDateTime } from '@/shared/format';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Колонка «Останній вхід» у переліку користувачів (`BE-12`).
 *
 * ⛔ Сервер віддає `lastSignInAt: string | null` — `null`, коли обліковий
 * запис ще ЖОДНОГО РАЗУ не входив. До цієї правки колонки не було взагалі:
 * `lastSignInAt` у відповіді ігнорувався мовчки.
 *
 * ⚠ `null` показується словом «never», а НЕ тире/порожнечею: «ніколи не
 * входив» — це відповідь, а не відсутність даних (`shared/ui/Timestamp`
 * дефолтить у тире саме для «ще не завантажилось», і тут його свідомо
 * перекрито через `fallback={t('sources.never')}` — той самий ключ, що вже
 * несе це значення для `CollectionScheduleTab`).
 *
 * ⚠ Заголовок колонки йде через `t('security.lastSignIn')`. Сід
 * (`09-seed.sql`) заводить інтегратор, а не ця гілка, і ключа там ще немає:
 * тест НЕ заводить `security.lastSignIn` у `SeededStrings` навмисно — це
 * робить `t()` невирішеним, і компонент показує позначений ключ
 * (`⟦security.lastSignIn⟧`), рівно так само, як бачить це `EndpointCoverageTests`
 * до появи рядка в сіді.
 */

const SeededStrings: Record<string, string> = {
  'security.login': 'Login',
  'security.name': 'Name',
  'security.kind': 'Kind',
  'security.userState': 'State',
  'security.access': 'Access',
  'security.alerts': 'Alerts',
  'security.mustChangePassword': 'Must change password',
  'security.lockedOut': 'Locked out',
  'security.bootstrap': 'Bootstrap',
  'security.noUsers': 'No users',
  'security.noUsersHint': 'No users hint',
  'security.alertsNeedEmail': 'Needs an email address',
  'sources.never': 'never',
};

const SignedInAt = '2026-03-14T09:30:00Z';

const UsersResponse = {
  items: [
    {
      id: 1,
      userName: 'signed.in',
      displayName: 'Signed In',
      provider: 'Windows',
      email: null,
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
      lastSignInAt: SignedInAt,
    },
    {
      id: 2,
      userName: 'never.signed.in',
      displayName: 'Never Signed In',
      provider: 'Windows',
      email: null,
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
      lastSignInAt: null,
    },
  ],
  nextCursor: null,
  totalCount: 2,
};

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/users')) {
        return new Response(JSON.stringify(UsersResponse), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/me')) {
        return new Response(
          JSON.stringify({
            userId: 0,
            userName: 'test',
            language: 'en',
            permissions: [],
            isSimulation: false,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      return new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

beforeEach(() => {
  stubFetch();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderUsersTab() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=users']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('SecurityPage: колонка «Останній вхід» (BE-12)', () => {
  it('показує час входу через <time>, а для null — «never», не тире й не порожнечу', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderUsersTab();

    const signedInRow = (await screen.findByText('signed.in')).closest('tr');
    const neverRow = (await screen.findByText('never.signed.in')).closest('tr');
    expect(signedInRow).not.toBeNull();
    expect(neverRow).not.toBeNull();

    // ⛔ Мутаційний доказ 1: значення показане через <time dateTime="…">, а не
    // сирий рядок і не порожня клітинка. Якби компонент друкував порожнечу
    // замість значення, цей вузол не знайшовся б.
    const timeNode = (signedInRow as HTMLElement).querySelector(
      `time[datetime="${SignedInAt}"]`,
    );
    expect(timeNode, '<time> зі значенням входу не знайдено').not.toBeNull();
    expect(timeNode?.textContent).toBe(formatDateTime(SignedInAt));
    expect(timeNode?.textContent).not.toBe(SignedInAt);

    // ⛔ Мутаційний доказ 2: `null` показано словом «never», а НЕ тире (`—`)
    // і НЕ порожньою клітинкою. Якби `null` малювався порожнім рядком чи
    // тире-дефолтом `Timestamp`, обидва наступні твердження впали б.
    expect(within(neverRow as HTMLElement).getByText('never')).not.toBeNull();
    expect(within(neverRow as HTMLElement).queryByText('—')).toBeNull();
  });

  it('заголовок ⟦security.lastSignIn⟧ стоїть рівно над колонкою значень (ключ без сіду)', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderUsersTab();

    const neverCell = await screen.findByText('never');
    const headerRow = neverCell.closest('table')?.querySelector('thead tr');
    const bodyRow = neverCell.closest('tr');

    expect(headerRow).not.toBeNull();
    expect(bodyRow).not.toBeNull();

    const headerCells = within(headerRow as HTMLElement).getAllByRole('columnheader');
    const bodyCells = within(bodyRow as HTMLElement).getAllByRole('cell');

    const columnIndex = bodyCells.findIndex((cell) => cell.contains(neverCell));
    expect(columnIndex).toBeGreaterThan(-1);
    // ⚠ Ключ `security.lastSignIn` навмисно ВІДСУТНІЙ у `SeededStrings` цього
    // файлу: сід заводить інтегратор. Доти `t()` бачить невирішений ключ і
    // повертає його позначеним — так само, як бачив би реальний каталог до
    // появи рядка в 09-seed.sql.
    expect(headerCells[columnIndex]?.textContent).toBe('⟦security.lastSignIn⟧');
  });
});
