import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectTemplateRoutes } from '@/features/approvalRoutes/ProjectTemplateRoutes';
import type { ProjectTemplateRouteDto } from '@/features/approvalRoutes/approvalRoutesApi';
import { testTheme } from '@/test/render';

/**
 * ✎ B155: налаштування проєкту → «Шаблони — погодження»: рядок на шаблон із `GET /projects/{id}/template-routes`,
 * а «Змінити виняток» відкриває редактор області `{kind:'projectTemplate'}` саме цього проєкту й шаблону.
 */
configure({ asyncUtilTimeout: 10_000 });

const Rows: ProjectTemplateRouteDto[] = [
  {
    templateId: 7,
    code: 'LAND',
    nameL10n: { values: { en: 'Land' } },
    workflowModeNone: false,
    effectiveSource: 'template',
    effectiveSteps: [
      { ordinal: 1, roleId: 11, isOptional: false },
      { ordinal: 2, roleId: 12, isOptional: false },
    ],
    generalApproverAlsoApproves: true,
  },
  {
    templateId: 8,
    code: 'HSE401',
    nameL10n: { values: { en: 'HSE 401' } },
    workflowModeNone: true,
    effectiveSource: 'none',
    effectiveSteps: [],
    generalApproverAlsoApproves: false,
  },
];

const Roles = [
  { id: 11, code: 'Reviewer', nameL10n: null, isBuiltIn: false, isGeneralApprover: false },
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
      const url = String(input);
      urls.push(url);

      if (url.endsWith('/api/v1/projects/3/template-routes')) return json(Rows);
      if (url.includes('/roles/approvers')) return json(Roles);
      if (url.endsWith('/api/v1/projects/3/templates/7/approval-route')) {
        return json({
          projectId: 3,
          templateId: 7,
          hasRoute: false,
          steps: [],
          effectiveSource: 'template',
          effectiveSteps: Rows[0]?.effectiveSteps ?? [],
          generalApproverAlsoApproves: true,
          generalApproverRoleId: 13,
          canEdit: true,
          workflowModeNone: false,
        });
      }

      return json([]);
    }),
  );

  return urls;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectTemplateRoutes projectId={3} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ProjectTemplateRoutes', () => {
  it('таблиця — у контейнері прокрутки: на 375 px сторінка не їде вбік (fg5, /projects/{id}/settings)', async () => {
    serve();
    show();

    const table = await screen.findByTestId('project-template-routes');
    expect(table.closest('[data-testid="project-template-routes-scroll"]'), 'таблиця поза контейнером прокрутки').not.toBeNull();
  });

  it('рядок на шаблон: код, назва, джерело, кроки, підказка Approver і «маршрут не діє»', async () => {
    serve();
    show();

    const table = await screen.findByTestId('project-template-routes');
    const land = table.querySelector('[data-template-id="7"]') as HTMLElement;
    const hse = table.querySelector('[data-template-id="8"]') as HTMLElement;

    expect(land.getAttribute('data-source')).toBe('template');
    expect(within(land).getByText('LAND')).toBeDefined();
    expect(within(land).getByText('Land')).toBeDefined();
    expect(within(land).getByTestId('project-template-route-source').textContent).toBe('⟦approvalRoutes.source.template⟧');
    await waitFor(() => expect(within(land).getByText('Reviewer → Controller')).toBeDefined());
    expect(within(land).getByText('⟦approvalRoutes.generalApproverHint⟧')).toBeDefined();

    expect(hse.getAttribute('data-source')).toBe('none');
    expect(within(hse).getByText('⟦workflow.routeNone⟧')).toBeDefined();
    expect(within(hse).getByText('⟦approvalRoutes.workflowModeNone⟧')).toBeDefined();
    expect(within(hse).queryByText('⟦approvalRoutes.generalApproverHint⟧')).toBeNull();
  });

  it('«Змінити виняток» відкриває редактор області проєкт × шаблон', async () => {
    const urls = serve();
    show();

    const button = await screen.findByRole('button', { name: '⟦approvalRoutes.editExceptionFor (template=LAND)⟧' });
    expect(button.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(button);

    expect(button.getAttribute('aria-expanded')).toBe('true');
    const editor = await screen.findByTestId('scoped-approval-route');
    expect(editor.getAttribute('data-source')).toBe('template');
    expect(urls.some((url) => url.endsWith('/api/v1/projects/3/templates/7/approval-route'))).toBe(true);
  });
});
