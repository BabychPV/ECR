import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Аудит-пас 5: скидання чернетки «New role»/«New user» жило ЛИШЕ в
 * `onSuccess` мутації — Cancel і закриття діалогу (`onClose`) лишали код/
 * назву в React-стані, і повторне відкриття діалогу показувало чернетку
 * ПОПЕРЕДНЬОЇ, скасованої спроби, а не порожню форму.
 */
const SeededStrings: Record<string, string> = {
  'security.roles': 'Roles',
  'security.grants': 'Grants',
  'security.users': 'Users',
  'security.createRole': 'New role',
  'security.roleCode': 'Code',
  'security.roleName': 'Name',
  'security.permissions': 'Permissions',
  'common.cancel': 'Cancel',
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
      if (url.includes('/me')) {
        return new Response(
          JSON.stringify({
            userId: 0,
            userName: 'test',
            language: 'en',
            permissions: ['Security.ManageRoles'],
            isSimulation: false,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/permissions')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/roles') || url.includes('/languages')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return new Response(JSON.stringify({ items: [], nextCursor: null, totalCount: 0 }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=roles']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('SecurityPage: скасування діалогу «New role» очищає чернетку', () => {
  it('Cancel очищає поле коду — повторне відкриття не показує стару чернетку', async () => {
    stubFetch();
    await loadCatalog('en', 'private');

    const user = userEvent.setup();
    show();

    await user.click(await screen.findByRole('button', { name: 'New role' }));

    const codeField = await screen.findByLabelText('Code');
    await user.type(codeField, 'DRAFT_CODE');
    expect((codeField as HTMLInputElement).value).toBe('DRAFT_CODE');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    // Діалог закрито — повторно відкриваємо.
    await user.click(await screen.findByRole('button', { name: 'New role' }));

    const reopenedCodeField = await screen.findByLabelText('Code');

    // ⛔ ЧЕРВОНИЙ до фіксу: поле досі несло `DRAFT_CODE` зі скасованої спроби.
    expect((reopenedCodeField as HTMLInputElement).value).toBe('');
  });
});
