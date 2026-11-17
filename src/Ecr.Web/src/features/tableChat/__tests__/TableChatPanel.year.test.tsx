import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { TableChatPanel } from '../TableChatPanel';
import { FakeChatServer, message } from './fakeChatServer';

/**
 * ✎ LAND-YEAR L7a (item 18 of the plan): the table chat of a year document (`YearWithMonthColumns`) has no "this period
 * only" filter — every message of the year, each tagged with its month; a monthly document keeps the filter.
 *
 * ⛔ Mutation proof (READY): `!yearLayout &&` around the filter → `true &&` — the first test turns red.
 */
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { userId: 1, permissions: ['Document.View', 'Document.Discuss'] } }),
}));

const Table = {
  allowsDynamicRows: false, copyOrdinal: 1, isCopyable: false, maxDynamicRows: null, sheetCode: 'A', sheetDefId: 1,
  sheetNameL10n: { values: { en: 'Sheet A' } }, sheetOrdinal: 0, tableCode: 'T1', tableDefId: 1, tableInstanceId: 10,
  tableNameL10n: { values: { en: 'Land' } }, tableOrdinal: 0,
} as DocumentTableDto;

const server = new FakeChatServer();

function Wrapper({ children }: { children: ReactNode }): ReactNode {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>{children}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

function mount(yearLayout: boolean | undefined): void {
  render(
    <Wrapper>
      <TableChatPanel
        documentId={7}
        periodKey={209901}
        table={Table}
        onClose={vi.fn()}
        takeFocus={false}
        {...(yearLayout === undefined ? {} : { yearLayout })}
      />
    </Wrapper>,
  );
}

beforeEach(() => {
  server.messages = [message(1, { periodKey: 209910 }), message(2, { periodKey: 209909 })];
  server.revision = 1;
  server.lastRead = 0;
  server.canWrite = true;
  server.canModerate = false;
  server.candidates = [];
  server.failNextPost = false;
  server.requests.length = 0;
  server.install();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('TableChatPanel: year document', () => {
  it('year: no period filter, every message of the year is asked for and tagged with its month', async () => {
    mount(true);
    await screen.findByText('message 1');

    expect(document.querySelector('[data-chat-filter]')).toBeNull();
    expect(server.of('GET', '/messages').every((request) => !request.params.has('periodKey'))).toBe(true);
    expect(Array.from(document.querySelectorAll('[data-chat-period]')).map((node) => node.textContent)).toHaveLength(2);
  });

  it('monthly: the "this period only" filter stays', async () => {
    mount(undefined);
    await screen.findByText('message 1');

    expect(document.querySelector('[data-chat-filter="period"]')).not.toBeNull();
  });
});
