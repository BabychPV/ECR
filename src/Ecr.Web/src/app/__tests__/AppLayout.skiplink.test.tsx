import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';

/**
 * Мутаційний тест на «Пропустити навігацію» (`Q-263`, `WCAG 2.4.1 Bypass
 * Blocks`).
 *
 * ⛔ Не axe-сканування (те лишається в `test/a11y.ts` через додане правило
 * `bypass`) — цей файл перевіряє САМУ поведінку, яку axe лише виявляє
 * непрямо (структурна перевірка не ловить «а фокус справді туди
 * перейшов»). Обидва потрібні: axe ловить регрес структури (посилання
 * зникло/не перше), цей тест ловить регрес поведінки (посилання є, але
 * не працює).
 *
 * Доведено RED→GREEN вручну (крок процесу): з тимчасово відкоченим
 * `SkipToContentLink`/`AppShell.Main` (без `id`/`tabIndex`) тест падає на
 * першому ж твердженні — посилання «Skip to main content» відсутнє в
 * дереві; з фіксом — проходить.
 */
const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: [
    'Template.Edit',
    'Registry.View',
    'Calculation.View',
    'Security.ManageRoles',
    'Period.Configure',
    'Integration.Manage',
    'System.ViewHealth',
    'Report.ViewRegulatory',
    'Security.ViewAudit',
    'System.ManageLocalization',
  ],
  isSimulation: false,
  mustChangePassword: false,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function renderAppLayout(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          { index: true, element: <div data-testid="page-content">Main page content</div> },
        ],
      },
    ],
    { initialEntries: ['/'] },
  );

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('AppLayout: «Пропустити навігацію» (Q-263)', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/api/v1/me')) return jsonResponse(MeResponse);
        if (url.includes('/ui-strings/')) {
          return jsonResponse({ languageCode: 'en', revision: 1, strings: {} });
        }

        return jsonResponse(null);
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('існує, є ПЕРШИМ фокусованим елементом сторінки, і активація переносить фокус у <main>', async () => {
    renderAppLayout();

    // Дочекатися: сесія приїхала, каркас домалювався (нав видно).
    const skipLink = await screen.findByRole('link', { name: /skip to main content/i });

    const user = userEvent.setup();

    // З самого початку документа — перший `Tab` має впіймати САМЕ це
    // посилання, а не Burger, бейдж симуляції чи пункт навігації.
    await user.tab();
    expect(document.activeElement).toBe(skipLink);

    // Ціль існує і НЕ є одним із пунктів навігації, що йдуть у DOM раніше.
    const main = document.getElementById('main-content');
    expect(main).not.toBeNull();
    expect(main?.tagName).toBe('MAIN');

    // Активація (Enter, а не клік мишею — саме так її й використовує
    // клавіатурний користувач без читалки) переносить фокус у `<main>`.
    await user.keyboard('{Enter}');

    expect(document.activeElement).toBe(main);
  });
});
