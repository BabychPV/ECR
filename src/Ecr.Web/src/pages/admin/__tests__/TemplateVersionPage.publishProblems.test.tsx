import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * A2-01: публікація шаблону з Error+Warning одного scope показувала в тості
 * лише «… the first is ECR-TMPL-4224 at position 0» — причина жила в
 * `diagnostics` відповіді, якого екран не читав. Тест доводить: перелік проблем
 * (код + текст мовою інтерфейсу за ключем і підстановками) стає видимим блоком,
 * а сирого «at position 0» на екрані немає.
 */

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.publish': 'Publish',
  'version.clone': 'Clone version',
  'version.publishHint': 'After this the structure is frozen.',
  'version.publishProblems': 'The version was not published. Problems to fix: {count}',
  'workflow.reason': 'Reason',
  'periodRules.title': 'Period access rules',
  'err.ECR-TMPL-4224.severityConflict':
    'Rules {ruleCode} ({severity}) and {otherRuleCode} ({otherSeverity}) apply to the same area of table {tableCode} with different severity levels.',
  'err.ECR-TMPL-4225.requiredNotCovered': 'Column {tableCode}.{columnCode} is required, but nothing checks it.',
  'err.ECR-TMPL-0422.publishRejected':
    'The template version cannot be published: {count} problem(s) found; the first is {code} at position {position}.',
};

const json = { 'Content-Type': 'application/json' };

/** Тіло 422 так, як його віддає сервер: розширення — плоскими полями. */
const PublishRefusal = {
  title: 'err.ECR-TMPL-0422',
  status: 422,
  errorCode: 'ECR-TMPL-0422',
  correlationId: 'c-1',
  detail: 'Rules LimitBlock (Error) and LimitWarn (Warning) apply to the same area of table Stacks with different severity levels.',
  messageKey: 'err.ECR-TMPL-4224.severityConflict',
  diagnostics: [
    {
      code: 'ECR-TMPL-4224',
      message: 'Правила LimitBlock (Error) і LimitWarn (Warning) діють на ту саму область.',
      position: 0,
      length: 1,
      messageKey: 'err.ECR-TMPL-4224.severityConflict',
      messageParams: {
        tableCode: 'Stacks',
        ruleCode: 'LimitBlock',
        severity: 'Error',
        otherRuleCode: 'LimitWarn',
        otherSeverity: 'Warning',
      },
    },
    {
      code: 'ECR-TMPL-4225',
      message: 'Колонка Stacks.Mass обов\'язкова, але її ніхто не перевіряє.',
      position: 0,
      length: 1,
      messageKey: 'err.ECR-TMPL-4225.requiredNotCovered',
      messageParams: { tableCode: 'Stacks', columnCode: 'Mass' },
    },
  ],
};

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }), {
          status: 200,
          headers: json,
        });
      }
      if (url.includes('/me')) {
        return new Response(
          JSON.stringify({
            userId: 0,
            userName: 'test',
            language: 'en',
            permissions: ['Template.Publish', 'Template.Edit'],
            isSimulation: false,
          }),
          { status: 200, headers: json },
        );
      }
      if (url.includes('/publish') && init?.method === 'POST') {
        return new Response(JSON.stringify(PublishRefusal), {
          status: 422,
          headers: { 'Content-Type': 'application/problem+json' },
        });
      }
      if (url.includes('/structure')) {
        return new Response(
          JSON.stringify({ isEditable: true, presentationRevision: 0, sheets: [], templateVersionId: 1 }),
          { status: 200, headers: json },
        );
      }
      if (url.includes('/versions?limit=')) {
        return new Response(
          JSON.stringify({
            items: [
              {
                id: 1,
                version: '1.0.0.0',
                status: 'Draft',
                presentationRevision: 0,
                clonedFromVersionId: null,
                publishedAt: null,
              },
            ],
            nextCursor: null,
            totalCount: null,
          }),
          { status: 200, headers: json },
        );
      }
      return new Response(JSON.stringify([]), { status: 200, headers: json });
    }),
  );
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class {
    observe() {}
    unobserve() {}
    disconnect() {}
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplateVersionPage: відмова публікації показує перелік проблем (A2-01)', () => {
  it('перелік із кодом і читабельним текстом кожної проблеми, без «at position 0»', async () => {
    stubFetch();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

    render(
      <MantineProvider theme={theme}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/templates/1/versions/1']}>
            <Routes>
              <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Reason'), { target: { value: 'Release' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Publish' }));

    const alert = await screen.findByRole('alert');

    expect(within(alert).getByText('The version was not published. Problems to fix: 2')).not.toBeNull();
    expect(within(alert).getByText('ECR-TMPL-4224')).not.toBeNull();
    expect(
      within(alert).getByText(
        /Rules LimitBlock \(Error\) and LimitWarn \(Warning\) apply to the same area of table Stacks/,
      ),
    ).not.toBeNull();
    expect(within(alert).getByText('ECR-TMPL-4225')).not.toBeNull();
    expect(within(alert).getByText(/Column Stacks\.Mass is required/)).not.toBeNull();

    await waitFor(() => {
      expect(screen.queryByText(/at position 0/)).toBeNull();
    });
  });
});
