import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectLifecycleTab } from '@/pages/projects/ProjectLifecycleTab';
import { testTheme } from '@/test/render';

/**
 * ✎ 25.11: «Delete project with all data…» на вкладці Lifecycle — лише з правом `Project.DeleteWithData`; діалог
 * показує документи з ключами, кількості й місяці; кнопка — лише після ТОЧНОГО коду й причини; залежні (B80) чи архів
 * (B69) — причина видима, видалення недоступне.
 *
 * Мутації (вручну, RED → GREEN): (1) у `ProjectLifecycleTab` показати рядок без `can(…'Project.DeleteWithData')` —
 * червоніє перший тест; (2) у `PurgeProjectModal` прибрати `typed === code` — червоніє «код набрано не точно»;
 * (3) прибрати `reason.trim().length > 0` — червоніє «без причини»; (4) прибрати `data.blocker !== null` — червоніє
 * «залежні».
 */

configure({ asyncUtilTimeout: 20_000 });

const Slow = 120_000;

interface Preview {
  readonly blocker: string | null;
  readonly dependants: { documentId: number; businessKey: string; projectCode: string; reason: string }[];
}

function respond(permissions: string[], preview: Preview = { blocker: null, dependants: [] }): { posts: { url: string; body: string }[] } {
  const state = { posts: [] as { url: string; body: string }[] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, code = 200): Response =>
        new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'POST') {
        state.posts.push({ url, body: String(init.body) });
        return new Response(null, { status: 204 });
      }
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:7': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'admin',
        });
      }
      if (url.includes('/deletion-preview')) {
        return json({
          projectId: 7,
          code: 'HSE401_2026',
          status: 'Active',
          documents: [
            { documentId: 101, businessKey: 'HSE401-A', templateCode: 'HSE400', cells: 40 },
            { documentId: 102, businessKey: 'HSE401-B', templateCode: null, cells: 2 },
          ],
          counts: {
            cells: 42, calculationResults: 7, calculationRuns: 2, calculationIssues: 1,
            approvalEvents: 5, approvalStates: 4, submissionSnapshots: 3, periods: 12,
          },
          dataMonths: [202601, 202602],
          archivedPeriodKeys: [],
          dependants: preview.dependants,
          blocker: preview.blocker,
        });
      }
      if (url.includes('/documents/summary')) {
        return json({ draft: 2, partlySubmitted: 0, submitted: 0, approved: 0, rejected: 0, withIssues: 0, noApproval: 0 });
      }
      if (url.includes('/periods')) {
        return json({
          projectId: 7, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'Asia/Aqtau',
          policy: { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
          periods: [],
        });
      }
      if (url.includes('/api/v1/projects')) {
        return json({
          items: [{ id: 7, code: 'HSE401_2026', status: 'Active', periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      return json(null);
    }),
  );

  return state;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/projects/7/lifecycle']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ProjectLifecycleTab projectId={7} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const Admin = ['Project.Manage', 'Project.DeleteWithData'];

async function openDialog(): Promise<HTMLElement> {
  await userEvent.click(await screen.findByRole('button', { name: '⟦projects.lifecycle.purge⟧' }));
  return screen.findByRole('dialog');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ProjectLifecycleTab: Delete project with all data (✎ 25.11)', () => {
  it(
    'без права Project.DeleteWithData рядка немає (Project.Manage не досить)',
    async () => {
      respond(['Project.Manage']);
      show();

      await screen.findByRole('button', { name: '⟦projects.lifecycle.delete⟧' });
      expect(screen.queryByRole('button', { name: '⟦projects.lifecycle.purge⟧' })).toBeNull();
      expect(document.querySelector('[data-lifecycle-action="purge"]')).toBeNull();
    },
    Slow,
  );

  it(
    'діалог показує документи з ключами, кількості й місяці; кнопка — лише після точного коду й причини; POST із тілом',
    async () => {
      const state = respond(Admin);
      show();
      const dialog = await openDialog();

      await within(dialog).findByText(/projects\.purge\.documentCells \(key=HSE401-A, template=HSE400, cells=40\)/);
      expect(within(dialog).getByText(/projects\.purge\.documentCells \(key=HSE401-B, template=—, cells=2\)/)).toBeTruthy();
      expect(dialog.querySelector('[data-purge-counts]')?.textContent).toContain('cells=42');
      expect(dialog.querySelector('[data-purge-counts]')?.textContent).toContain('events=5');
      expect(dialog.querySelector('[data-purge-irreversible]')).not.toBeNull();

      const confirm = within(dialog).getByRole('button', { name: '⟦projects.purge.confirm⟧' });
      const code = within(dialog).getByRole('textbox', { name: /projects\.purge\.typeCode/ });
      const reason = within(dialog).getByRole('textbox', { name: /projects\.purge\.reason/ });

      // ✎ Підпис — звичайний текст; моноширинний лише сам код (елемент Code у підписі), а не весь підпис.
      const label = dialog.querySelector(`label[for="${code.id}"]`);
      expect(label?.querySelector('[data-purge-code]')?.textContent).toBe('HSE401_2026');
      expect(label?.closest('[style*="monospace"]')).toBeNull();

      // Код набрано не точно (регістр) — недоступна.
      await userEvent.type(code, 'hse401_2026');
      await userEvent.type(reason, 'тестовий проєкт');
      expect(confirm.hasAttribute('disabled')).toBe(true);

      // Точний код, але без причини — недоступна.
      await userEvent.clear(code);
      await userEvent.type(code, 'HSE401_2026');
      await userEvent.clear(reason);
      expect(confirm.hasAttribute('disabled')).toBe(true);

      await userEvent.type(reason, 'тестовий проєкт');
      await waitFor(() => expect(confirm.hasAttribute('disabled')).toBe(false));
      await userEvent.click(confirm);

      await waitFor(() => expect(state.posts.map((p) => p.url)).toEqual(['/api/v1/projects/7/purge']));
      expect(JSON.parse(state.posts[0]!.body)).toEqual({ code: 'HSE401_2026', reason: 'тестовий проєкт' });
    },
    Slow,
  );

  it(
    'залежні документи інших проєктів (B80): перелік видно, видалення недоступне',
    async () => {
      const state = respond(Admin, {
        blocker: 'err.ECR-PRD-0409.purgeHasDependants',
        dependants: [{ documentId: 501, businessKey: 'LAND-2026', projectCode: 'LANDUI2026', reason: 'SourceBinding:HSE400:2026' }],
      });
      show();
      const dialog = await openDialog();

      const blocker = await waitFor(() => {
        const node = dialog.querySelector('[data-purge-blocker="dependants"]');
        expect(node).not.toBeNull();
        return node!;
      });
      expect(blocker.textContent).toContain('project=LANDUI2026, key=LAND-2026');
      expect(within(dialog).getByRole('textbox', { name: /projects\.purge\.typeCode/ }).hasAttribute('disabled')).toBe(true);
      expect(within(dialog).getByRole('button', { name: '⟦projects.purge.confirm⟧' }).hasAttribute('disabled')).toBe(true);
      expect(state.posts).toEqual([]);
    },
    Slow,
  );
});
