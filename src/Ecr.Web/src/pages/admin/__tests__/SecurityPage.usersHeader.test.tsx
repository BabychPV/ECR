import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';

/**
 * Таблиця користувачів (`Q-286`): заголовок «State» стояв над колонкою, що
 * насправді малює кнопку «Access», а справжні індикатори стану (разовий
 * пароль/блокування/bootstrap) сиділи в останній колонці зовсім БЕЗ
 * заголовка. Обидва ключі каталогу (`security.userState`, `security.access`)
 * уже існували — застосовані лише не до тих колонок.
 *
 * Тест рендерить справжню сторінку (вкладка `users` через адресу — так само,
 * як обирає її сам компонент, `useUrlState`) зі СПРАВЖНІМ каталогом рядків
 * (мокований `fetch`, ті самі значення, що сервер бере з `09-seed.sql`) і
 * одним користувачем з усіма трьома бейджами стану ввімкненими — щоб
 * перевірити не лише текст заголовка, а й те, що зміст ПІД ним відповідає.
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
};

const UsersResponse = {
  items: [
    {
      id: 1,
      userName: 'jdoe',
      displayName: 'Jane Doe',
      provider: 'Windows',
      email: null,
      isActive: true,
      isBootstrapAdmin: true,
      isLockedOut: true,
      mustChangePassword: true,
      receivesAlerts: false,
    },
  ],
  nextCursor: null,
  totalCount: 1,
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
        // ⛔ `useSession`/`can()` читає `.permissions` без захисту від
        // `undefined` — відповідь мусить бути ОБ'ЄКТОМ очікуваної форми, а не
        // порожнім масивом-заглушкою (інакше рендер падає до того, як
        // дійде до таблиці, яку цей тест перевіряє).
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
      // roles/permissions — незалежні запити тієї самої сторінки (`SecurityPage`
      // монтує їх безумовно), порожній список цілком годящий: ця картка їх не
      // зачіпає.
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
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=users']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('SecurityPage: заголовки таблиці користувачів (Q-286)', () => {
  it('заголовок "Access" стоїть над колонкою з кнопкою «Access», не над бейджами стану', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderUsersTab();

    const accessButton = await screen.findByRole('button', { name: 'Access' });
    const headerRow = accessButton.closest('table')?.querySelector('thead tr');
    const bodyRow = accessButton.closest('tr');

    expect(headerRow).not.toBeNull();
    expect(bodyRow).not.toBeNull();

    const headerCells = within(headerRow as HTMLElement).getAllByRole('columnheader');
    const bodyCells = within(bodyRow as HTMLElement).getAllByRole('cell');

    // ⛔ Мутаційний доказ: індекс колонки кнопки «Access» у ТІЛІ мусить
    // збігатися з індексом заголовка «Access» у ШАПЦІ. Якщо ключі знову
    // переплутати (повернути `security.userState` над цією колонкою), цей
    // рядок впаде першим.
    const accessColumnIndex = bodyCells.findIndex((cell) => cell.contains(accessButton));
    expect(accessColumnIndex).toBeGreaterThan(-1);
    expect(headerCells[accessColumnIndex]?.textContent).toBe('Access');
  });

  it('заголовок "State" стоїть над колонкою з бейджами стану (разовий пароль/блокування/bootstrap)', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderUsersTab();

    const mustChangeBadge = await screen.findByText('Must change password');
    const headerRow = mustChangeBadge.closest('table')?.querySelector('thead tr');
    const bodyRow = mustChangeBadge.closest('tr');

    expect(headerRow).not.toBeNull();
    expect(bodyRow).not.toBeNull();

    const headerCells = within(headerRow as HTMLElement).getAllByRole('columnheader');
    const bodyCells = within(bodyRow as HTMLElement).getAllByRole('cell');

    const stateColumnIndex = bodyCells.findIndex((cell) => cell.contains(mustChangeBadge));
    expect(stateColumnIndex).toBeGreaterThan(-1);

    // ⛔ Раніше ця колонка мала `<Table.Th />` — без жодного тексту. Тест
    // падає і на порожній заголовок, і на «State», що знову з'явився б над
    // кнопкою «Access» замість цієї колонки.
    expect(headerCells[stateColumnIndex]?.textContent).toBe('State');
  });
});
