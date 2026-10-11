import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * FP-3 (прохід «від першого обличчя» 2026-10-11, P2): користувач без жодного гранта на проєкт
 * (`SystemAdministrator`/`TemplateAdministrator`/`PeriodAdministrator` на свіжій системі) бачив
 * «документів немає — вони з'являться, щойно період відкрито» — підказку про ПЕРІОД там, де
 * справжня причина — ДОСТУП. Перелік проєктів порожній лише за відсутності гранта.
 *
 * ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати `noProjects` з `emptyTitle` — перший тест червоніє.
 */
configure({ asyncUtilTimeout: 10_000 });

const Find = { timeout: 10_000 };

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function serve(options: { readonly projects: readonly unknown[]; readonly projectsFail?: boolean }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View', 'Document.Create'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/projects')) {
        if (options.projectsFail === true) {
          return json({ title: 'Server error', status: 500, errorCode: 'ECR-SYS-0500', correlationId: 'c' }, 500);
        }

        return json({ items: options.projects, nextCursor: null, totalCount: options.projects.length });
      }

      if (url.includes('/api/v1/documents')) {
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage — порожній стан без жодного проєкту (FP-3)', () => {
  it('проєктів немає: «немає доступу до жодного проєкту», а не «документів немає — відкрийте період»', async () => {
    serve({ projects: [] });
    show();

    // ⚠ Текст стоїть двічі: заголовок порожнього стану і підказка недоступної кнопки.
    expect((await screen.findAllByText('⟦documents.noProjects⟧', {}, Find)).length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('⟦documents.noProjectsHint⟧')).toBeTruthy();
    expect(screen.queryByText('⟦documents.empty⟧')).toBeNull();
    expect(screen.queryByText('⟦documents.emptyHint⟧')).toBeNull();

    // «New document» не активна: обрати проєкт неможливо.
    const create = screen.getByRole('button', { name: '⟦documents.create⟧' });
    expect(create.hasAttribute('disabled') || create.getAttribute('data-disabled') === 'true').toBe(true);
  });

  it('проєкт є, документів немає — колишній текст про період, кнопка активна', async () => {
    serve({ projects: [{ id: 1, code: 'PRJ', status: 'Active' }] });
    show();

    expect(await screen.findByText('⟦documents.empty⟧', {}, Find)).toBeTruthy();
    expect(screen.queryByText('⟦documents.noProjects⟧')).toBeNull();

    const create = screen.getByRole('button', { name: '⟦documents.create⟧' });
    expect(create.hasAttribute('disabled')).toBe(false);
  });

  it('перелік проєктів не прочитано — тверджень про доступ немає (не «немає проєктів»)', async () => {
    serve({ projects: [], projectsFail: true });
    show();

    expect(await screen.findByText('⟦documents.empty⟧', {}, Find)).toBeTruthy();
    expect(screen.queryByText('⟦documents.noProjects⟧')).toBeNull();
  });
});
