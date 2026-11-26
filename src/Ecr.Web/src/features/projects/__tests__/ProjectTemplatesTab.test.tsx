import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectTemplatesTab } from '@/features/projects/ProjectTemplatesTab';
import { ProjectSettingsPage } from '@/pages/admin/ProjectSettingsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ 24.11 (рішення людини «Одна таблиця шаблонів»): вкладка проєкту Templates — маршрут погодження проєкту одним
 * рядком і ОДНА таблиця шаблонів: версія, документи, погодження (джерело маршруту, що діє, і кроки), «Edit
 * exception» (редактор винятку проєкт × шаблон під рядком) і «Detach» з колишніми правилами. Поточний період,
 * політика й часовий пояс — на вкладці General; другої таблиці «Templates: approval» немає.
 *
 * Проєкт 7: GEN (основний, 3 документи, маршрут проєкту), LAND (0 документів, виняток проєкту), HSE (без подання).
 */
configure({ asyncUtilTimeout: 30_000 });

const Templates = [
  {
    projectTemplateId: 1, templateId: 4, templateCode: 'GEN', templateNameL10n: { values: { en: 'General' } },
    currentTemplateVersionId: 40, version: '2.3.0', layout: 'PerMonthCopies', isPrimary: true, documentCount: 3,
  },
  {
    projectTemplateId: 2, templateId: 8, templateCode: 'LAND', templateNameL10n: { values: { en: 'Land' } },
    currentTemplateVersionId: 80, version: '1.0', layout: 'YearWithMonthColumns', isPrimary: false, documentCount: 0,
  },
  {
    projectTemplateId: 3, templateId: 9, templateCode: 'HSE', templateNameL10n: { values: { en: 'Hazard' } },
    currentTemplateVersionId: 90, version: '1.1', layout: 'PerMonthCopies', isPrimary: false, documentCount: 0,
  },
];

const Routes = [
  {
    templateId: 4, code: 'GEN', nameL10n: { values: { en: 'General' } }, workflowModeNone: false,
    effectiveSource: 'project', effectiveSteps: [{ ordinal: 1, roleId: 11, isOptional: false }], generalApproverAlsoApproves: false,
  },
  {
    templateId: 8, code: 'LAND', nameL10n: { values: { en: 'Land' } }, workflowModeNone: false,
    effectiveSource: 'own',
    effectiveSteps: [
      { ordinal: 1, roleId: 11, isOptional: false },
      { ordinal: 2, roleId: 12, isOptional: false },
    ],
    generalApproverAlsoApproves: false,
  },
  {
    templateId: 9, code: 'HSE', nameL10n: { values: { en: 'Hazard' } }, workflowModeNone: true,
    effectiveSource: 'none', effectiveSteps: [], generalApproverAlsoApproves: false,
  },
];

const Roles = [
  { id: 11, code: 'Approver', nameL10n: null, isBuiltIn: true, isGeneralApprover: false },
  { id: 12, code: 'Controller', nameL10n: null, isBuiltIn: false, isGeneralApprover: false },
];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function serve(): string[] {
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input).split('?')[0] ?? '';
      urls.push(url);

      if (url.endsWith('/api/v1/projects/7/templates')) return json(Templates);
      if (url.endsWith('/api/v1/projects/7/template-routes')) return json(Routes);
      if (url.endsWith('/api/v1/roles/approvers')) return json(Roles);
      if (url.endsWith('/api/v1/projects/7/approval-route')) {
        return json({ projectId: 7, hasRoute: true, steps: [{ ordinal: 1, roleId: 11, isOptional: false }] });
      }
      if (url.endsWith('/api/v1/projects/7/templates/8/approval-route')) {
        return json({
          projectId: 7, templateId: 8, hasRoute: true, steps: Routes[1]?.effectiveSteps ?? [], effectiveSource: 'own',
          effectiveSteps: Routes[1]?.effectiveSteps ?? [], generalApproverAlsoApproves: false, generalApproverRoleId: null,
          canEdit: true, workflowModeNone: false,
        });
      }
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1, userName: 'admin', language: 'en', permissions: ['Project.Manage', 'Period.Configure'], isSimulation: false,
          denies: [], grants: { 'Project:7': 'Manage' }, mustChangePassword: false, simulatedForUserId: null,
        });
      }
      if (url.endsWith('/api/v1/projects/7/periods')) {
        return json({ projectId: 7, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'Asia/Aqtau', policy: null, periods: [] });
      }
      if (url.endsWith('/api/v1/projects')) {
        return json({
          items: [{ id: 7, code: 'PRJ-7', status: 'Draft', periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      return json(null);
    }),
  );

  return urls;
}

function wrap(children: JSX.Element): JSX.Element {
  return (
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>
  );
}

const row = (code: string): HTMLElement => document.querySelector(`[data-project-template="${code}"]`) as HTMLElement;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('✎ 24.11: вкладка Templates — одна таблиця шаблонів з погодженням', () => {
  it('одна таблиця: у рядку шаблону — версія, документи і погодження (джерело + кроки); маршрут проєкту — одним рядком', async () => {
    serve();
    render(wrap(<ProjectTemplatesTab projectId={7} readOnly={false} managesProject />));

    await screen.findByTestId('project-templates');
    expect(document.querySelectorAll('table')).toHaveLength(1);
    expect(screen.queryByTestId('project-template-routes')).toBeNull();
    expect(screen.getByRole('columnheader', { name: '⟦projects.templates.approval⟧' })).toBeTruthy();

    await waitFor(() => expect(within(row('GEN')).getByText('⟦approvalRoutes.source.project⟧')).toBeTruthy());
    expect(within(row('GEN')).getByText('2.3.0')).toBeTruthy();
    await waitFor(() => expect(within(row('GEN')).getByText('Approver')).toBeTruthy());
    expect(within(row('LAND')).getByText('⟦approvalRoutes.source.own⟧')).toBeTruthy();
    expect(within(row('LAND')).getByText('Approver → Controller')).toBeTruthy();
    expect(within(row('HSE')).getByText('⟦approvalRoutes.source.none⟧')).toBeTruthy();
    expect(within(row('HSE')).getByText('⟦approvalRoutes.workflowModeNone⟧')).toBeTruthy();

    const route = document.querySelector('[data-project-route]') as HTMLElement;
    expect(route.textContent).toContain('⟦projects.templates.routeForAll⟧');
    await waitFor(() => expect(route.querySelector('[data-project-route-steps]')?.textContent).toBe('Approver'));
    expect(within(route).getByRole('button', { name: '⟦projectSettings.routeAction⟧' })).toBeTruthy();
  });

  it('«Edit exception» у рядку відкриває редактор винятку проєкт × шаблон під цим рядком; Detach — у тій самій клітинці дій', async () => {
    const urls = serve();
    render(wrap(<ProjectTemplatesTab projectId={7} readOnly={false} managesProject />));
    await screen.findByTestId('project-templates');

    const exception = within(row('LAND')).getByRole('button', { name: '⟦approvalRoutes.editExceptionFor (template=LAND)⟧' });
    expect(exception.getAttribute('aria-expanded')).toBe('false');
    expect(within(row('LAND')).getByRole('button', { name: '⟦projects.templates.detach⟧ LAND' })).toBeTruthy();
    // Основний шаблон — «Detach» недоступний, як і раніше.
    expect(within(row('GEN')).getByRole('button', { name: '⟦projects.templates.detach⟧ GEN' }).getAttribute('aria-disabled')).toBe('true');

    fireEvent.click(exception);
    expect(exception.getAttribute('aria-expanded')).toBe('true');
    const expansion = document.querySelector('[data-project-template-expansion="LAND"]') as HTMLElement;
    expect(expansion.previousElementSibling).toBe(row('LAND'));
    expect(await within(expansion).findByTestId('scoped-approval-route')).toBeTruthy();
    expect((expansion.querySelector('td') as HTMLTableCellElement).colSpan).toBe(5);
    await waitFor(() => expect(urls).toContain('/api/v1/projects/7/templates/8/approval-route'));
  });

  it('без права керувати проєктом: таблиця без погодження, без рядка маршруту й без запитів маршрутів', async () => {
    const urls = serve();
    render(wrap(<ProjectTemplatesTab projectId={7} readOnly managesProject={false} />));

    await screen.findByTestId('project-templates');
    expect(screen.queryByRole('columnheader', { name: '⟦projects.templates.approval⟧' })).toBeNull();
    expect(document.querySelector('[data-project-route]')).toBeNull();
    expect(screen.queryAllByRole('button', { name: /editException|projects\.templates\.detach/ })).toHaveLength(0);
    expect(urls.some((url) => url.includes('template-routes') || url.includes('approval-route'))).toBe(false);
  });
});

describe('✎ 24.11: ProjectSettingsPage — вкладки Templates і General', () => {
  it('Templates: шаблони з погодженням, без поточного періоду й часового поясу', async () => {
    serve();
    render(wrap(<ProjectSettingsPage projectId={7} view="templates" />));

    await screen.findByTestId('project-templates');
    expect(document.querySelector('[data-project-section="templates"]')).not.toBeNull();
    expect(document.querySelector('[data-project-section="current-period"]')).toBeNull();
    expect(document.querySelector('[data-project-section="time-zone"]')).toBeNull();
    expect(document.querySelector('[data-project-section="template-routes"]')).toBeNull();
  });

  it('General: поточний період, політика і часовий пояс, без таблиці шаблонів', async () => {
    serve();
    render(wrap(<ProjectSettingsPage projectId={7} view="general" />));

    await waitFor(() => expect(document.querySelector('[data-project-section="current-period"]')).not.toBeNull());
    expect(document.querySelector('[data-project-section="policy"]')).not.toBeNull();
    expect(document.querySelector('[data-project-section="time-zone"]')?.textContent).toContain('Asia/Aqtau');
    expect(document.querySelector('[data-project-section="templates"]')).toBeNull();
    expect(document.querySelector('[data-project-section="route"]')).toBeNull();
  });
});
