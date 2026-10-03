import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { currentProjectPeriodKey } from '@/features/documents/useProjectCurrentPeriodDefault';
import { testTheme } from '@/test/render';

/**
 * T2-12: документ без `?periodKey` відкривався на календарному місяці (UTC), а не на поточному
 * періоді проєкту. Період проєкту тут ЯВНО відмінний від будь-якого календарного місяця «сьогодні».
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const ProjectCurrent = 201503;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(isCurrent: boolean): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);

    if (url.includes('/api/v1/me')) {
      return jsonResponse({
        denies: [],
        grants: { 'Project:1': 'Manage' },
        isSimulation: false,
        language: 'en',
        mustChangePassword: false,
        permissions: ['Document.View'],
        simulatedForUserId: null,
        userId: 1,
        userName: 'tester',
      });
    }

    if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });

    if (url.includes('/api/v1/projects/1/periods')) {
      return jsonResponse({
        projectId: 1,
        periodKind: 'Monthly',
        currentPeriodMode: 'Auto',
        timeZoneId: 'UTC',
        policy: {},
        periods: [
          { id: 5, periodKey: ProjectCurrent, state: 'Open', isCurrent, year: 2015, sequence: 3, startsAt: '2015-03-01T00:00:00Z', endsAt: '2015-04-01T00:00:00Z', graceEndsAt: null, reopenedUntil: null },
        ],
      });
    }

    if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: ProjectCurrent, messages: [], validated: false });
    if (url.includes('/tables/status')) return jsonResponse([]);
    if (url.includes('/tables')) return jsonResponse([]);

    if (url.includes('/api/v1/documents/1')) {
      return jsonResponse({
        businessKey: 'DOC-0001',
        createdAt: '2026-01-01T00:00:00Z',
        id: 1,
        nameL10n: { values: {} },
        projectId: 1,
        sheetCount: 1,
        sheetStates: {},
        hasLateEdits: false,
      });
    }

    return jsonResponse(null);
  });

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

function show(initial: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[initial]}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const calledUrls = (fetchMock: ReturnType<typeof vi.fn>): string[] =>
  fetchMock.mock.calls.map((call: unknown[]) => String(call[0]));

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('currentProjectPeriodKey', () => {
  it('бере період з позначкою isCurrent, без позначки — null', () => {
    expect(currentProjectPeriodKey([{ periodKey: 1 }, { periodKey: 2, isCurrent: true }])).toBe(2);
    expect(currentProjectPeriodKey([{ periodKey: 1, isCurrent: false }])).toBeNull();
    expect(currentProjectPeriodKey(undefined)).toBeNull();
  });
});

describe('DocumentPage: період за замовчуванням — поточний період проєкту', () => {
  it(
    'без ?periodKey дані документа запитуються за поточним періодом проєкту, а не за календарним місяцем',
    async () => {
      const fetchMock = mockFetch(true);
      show('/documents/1');

      await waitFor(
        () => {
          expect(calledUrls(fetchMock)).toContain(`/api/v1/documents/1/tables?periodKey=${String(ProjectCurrent)}`);
        },
        { timeout: SlowEnvTimeout },
      );
      expect(await screen.findByRole('heading', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'період у посиланні — рішення людини: календар його не перебиває',
    async () => {
      const fetchMock = mockFetch(true);
      show('/documents/1?periodKey=202001');

      await waitFor(
        () => {
          expect(calledUrls(fetchMock).some((url) => url.includes('/api/v1/projects/1/periods'))).toBe(true);
        },
        { timeout: SlowEnvTimeout },
      );

      expect(calledUrls(fetchMock).some((url) => url.includes(`periodKey=${String(ProjectCurrent)}`))).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'календар без поточного періоду — лишається тимчасове значення (календарний місяць)',
    async () => {
      const fetchMock = mockFetch(false);
      show('/documents/1');

      await waitFor(
        () => {
          expect(calledUrls(fetchMock).some((url) => url.includes('/api/v1/projects/1/periods'))).toBe(true);
        },
        { timeout: SlowEnvTimeout },
      );

      expect(calledUrls(fetchMock).some((url) => url.includes(`periodKey=${String(ProjectCurrent)}`))).toBe(false);
    },
    SlowEnvTimeout,
  );
});
