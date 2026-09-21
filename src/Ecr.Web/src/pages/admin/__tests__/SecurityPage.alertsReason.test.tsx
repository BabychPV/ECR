import { describe, it, expect, afterEach, beforeAll, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Перемикач алертів користувача без пошти вимкнений (`D-125`). Причину
 * («спершу задайте адресу») показував Mantine `Tooltip` — лише під мишею. Але
 * вимкнений `<input>` не фокусується, тож з клавіатури причину не було видно
 * НІКОЛИ, а читач чув лише «вимкнено».
 *
 * ⛔ Мутаційний доказ: повернути `Tooltip` у `SecurityPage.tsx` — червоні і
 * опис, і видимий текст.
 */
const Users = {
  items: [
    {
      id: 1,
      userName: 'with.mail',
      displayName: 'With Mail',
      provider: 'Windows',
      email: 'with@example.com',
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
    },
    {
      id: 2,
      userName: 'no.mail',
      displayName: 'No Mail',
      provider: 'Windows',
      email: null,
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
    },
  ],
  nextCursor: null,
  totalCount: 2,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/roles')) return Promise.resolve(jsonResponse([]));
      if (url.includes('/users')) return Promise.resolve(jsonResponse(Users));

      if (url.includes('/me')) {
        return Promise.resolve(
          jsonResponse({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: ['Security.ManageUsers'],
            simulatedForUserId: null,
            userId: 9,
            userName: 'tester',
          }),
        );
      }

      return Promise.resolve(jsonResponse([]));
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=users']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function alertsSwitchOf(userName: string): HTMLInputElement {
  return screen.getByLabelText(new RegExp(`security\\.alerts.*${userName}`)) as HTMLInputElement;
}

/** Текст, на який вказує `aria-describedby` елемента. */
function description(el: HTMLElement): string {
  return (el.getAttribute('aria-describedby') ?? '')
    .split(' ')
    .filter(Boolean)
    .map((id) => document.getElementById(id)?.textContent ?? '')
    .join(' ');
}

/** Елемент і всі його предки видимі — не `hidden`, не `display:none`. */
function isShown(el: HTMLElement): boolean {
  for (let node: HTMLElement | null = el; node !== null; node = node.parentElement) {
    if (node.hidden || getComputedStyle(node).display === 'none') return false;
  }

  return true;
}

/**
 * ⚠ Вкладка користувачів монтує поруч лінивий `GroupAssignmentsPanel`; його
 * холодний `import()` (30–130 мс у спокої, 300–540 мс під навантаженням)
 * інакше йшов усередині таймауту тесту. Прогрівається модуль, не обхід.
 */
beforeAll(async () => {
  await import('@/features/security/GroupAssignmentsPanel');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SecurityPage: причина вимкненого перемикача алертів доступна без миші', () => {
  it('без пошти: перемикач вимкнений, причина — видимий текст і опис перемикача', async () => {
    stubFetch();
    show();

    await screen.findByText('no.mail');

    const toggle = alertsSwitchOf('no.mail');
    expect(toggle.disabled).toBe(true);

    // Читач: опис прив'язаний до самого перемикача (вимкнені елементи
    // лишаються в дереві доступності й читаються навігацією читача).
    expect(description(toggle)).toMatch(/security\.alertsNeedEmail/);

    // Зрячий користувач клавіатури: причину видно без наведення.
    const reason = document.getElementById(toggle.getAttribute('aria-describedby') ?? '');
    expect(reason).not.toBeNull();
    expect(isShown(reason as HTMLElement)).toBe(true);
  });

  it('з поштою: перемикач активний, причини немає ні на екрані, ні в описі', async () => {
    stubFetch();
    show();

    await screen.findByText('with.mail');

    const toggle = alertsSwitchOf('with.mail');
    expect(toggle.disabled).toBe(false);
    expect(toggle.hasAttribute('aria-describedby')).toBe(false);

    // Рівно одна видима причина на сторінці — у рядку без пошти.
    expect(screen.getAllByText(/security\.alertsNeedEmail/)).toHaveLength(1);
  });
});
