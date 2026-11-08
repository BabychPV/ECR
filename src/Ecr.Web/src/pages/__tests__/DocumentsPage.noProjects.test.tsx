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

      // ✎ Projects/Documents 08.11: календар проєкту — для підказки порожнього року (які проєкти мають цей рік).
      if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) {
        const year = new Date().getFullYear();
        return json({ periods: [{ periodKey: year * 100 + 1, year, sequence: 1, state: 'Open' }] });
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
    // ✎ Projects/Documents 08.11: порожній стан року — `documents.emptyYear` замість прибраного `documents.empty`.
    expect(screen.queryByText(/documents\.emptyYear/)).toBeNull();

    // «New document» не активна: обрати проєкт неможливо.
    const create = screen.getByRole('button', { name: '⟦documents.create⟧' });
    expect(create.hasAttribute('disabled') || create.getAttribute('data-disabled') === 'true').toBe(true);
  });

  // ✎ Projects/Documents 08.11: перелік лише за рік — порожній стан «за рік N документів немає» з проєктами цього року.
  it('проєкт є, документів немає — текст про порожній рік із проєктом цього року, кнопка активна', async () => {
    serve({ projects: [{ id: 1, code: 'PRJ', status: 'Active' }] });
    show();
    const year = String(new Date().getFullYear());

    expect(await screen.findByText(`⟦documents.emptyYear (year=${year})⟧`, {}, Find)).toBeTruthy();
    expect(
      await screen.findByText(`⟦documents.emptyYearProjects (year=${year}, projects=PRJ (⟦status.project.Active⟧))⟧`, {}, Find),
    ).toBeTruthy();
    expect(screen.queryByText('⟦documents.noProjects⟧')).toBeNull();

    const create = screen.getByRole('button', { name: '⟦documents.create⟧' });
    expect(create.hasAttribute('disabled')).toBe(false);
  });

  it('перелік проєктів не прочитано — тверджень про доступ немає (не «немає проєктів»)', async () => {
    serve({ projects: [], projectsFail: true });
    show();
    const year = String(new Date().getFullYear());

    // ✎ Projects/Documents 08.11: нейтральний текст — тепер порожній рік (`documents.emptyYear`), без переліку проєктів.
    expect(await screen.findByText(`⟦documents.emptyYear (year=${year})⟧`, {}, Find)).toBeTruthy();
    expect(screen.getByText(`⟦documents.emptyYearNoProject (year=${year})⟧`)).toBeTruthy();
    expect(screen.queryByText('⟦documents.noProjects⟧')).toBeNull();
  });
});
