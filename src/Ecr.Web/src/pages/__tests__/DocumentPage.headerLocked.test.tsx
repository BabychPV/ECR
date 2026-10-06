import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * AN-39 / L8-13: поля шапки були активні в симуляції, в архівному проєкті і для
 * поданого/затвердженого документа - сервер (`EditRules.CanEdit`) їх відхиляв.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

interface Scenario {
  sheetState: string;
  isSimulation: boolean;
  projectStatus: string;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(scenario: Scenario): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: { 'Project:1': 'Write' },
          isSimulation: scenario.isSimulation,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/validation')) {
        return jsonResponse({ title: 'Not found', status: 404, detail: 'x', errorCode: 'ECR-DOC-0404' }, 404);
      }

      if (url.endsWith('/api/v1/documents/1/header')) {
        return jsonResponse({
          version: 'H1',
          fields: [
            { code: 'NOTE', dataType: 'String', headerFieldDefId: 1, isRequired: false, label: { values: { en: 'Header note' } }, value: 'abc' },
          ],
        });
      }

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false, maxDynamicRows: null, sheetCode: 'GEN', sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } }, sheetOrdinal: 0, tableCode: 'T1', tableDefId: 1,
            tableInstanceId: 1, tableNameL10n: { values: { en: 'Table 1' } }, tableOrdinal: 0,
          },
        ]);
      }

      if (url.includes('/api/v1/projects')) {
        return jsonResponse({
          items: [{ id: 1, code: 'PRJ', status: scenario.projectStatus }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} },
          projectId: 1, sheetCount: 1, sheetStates: { GEN: scenario.sheetState },
        });
      }

      return jsonResponse(null);
    }),
  );
}

async function show(scenario: Scenario): Promise<HTMLInputElement> {
  mockFetch(scenario);
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/documents/1?periodKey=202401']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );

  // ✎ UI-16: шапка згорнута, поки нічого не вимагає уваги — розгорнути.
  const toggle = await screen.findByTestId('document-header-toggle', {}, { timeout: SlowEnvTimeout });
  if (toggle.getAttribute('aria-expanded') === 'false') fireEvent.click(toggle);

  const field = await screen.findByRole('textbox', { name: /Header note/ }, { timeout: SlowEnvTimeout });

  return field as HTMLInputElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('DocumentPage: шапка недоступна для правки (L8-13)', () => {
  it('контроль: чернетка, гранти Write - поле активне', async () => {
    const field = await show({ sheetState: 'Draft', isSimulation: false, projectStatus: 'Active' });

    await waitFor(() => expect(field.disabled).toBe(false));
  }, SlowEnvTimeout);

  it.each([
    ['поданий аркуш', { sheetState: 'Submitted', isSimulation: false, projectStatus: 'Active' }],
    ['симуляція', { sheetState: 'Draft', isSimulation: true, projectStatus: 'Active' }],
    ['архівний проєкт', { sheetState: 'Draft', isSimulation: false, projectStatus: 'Archived' }],
  ])('%s - поле вимкнене', async (_name, scenario) => {
    const field = await show(scenario);

    await waitFor(() => expect(field.disabled).toBe(true));
  }, SlowEnvTimeout);
});
