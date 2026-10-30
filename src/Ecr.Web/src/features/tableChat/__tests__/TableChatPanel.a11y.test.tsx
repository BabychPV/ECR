import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { mantineProviderProps } from '@/shared/theme/provider';
import { Catalog, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { publishSummary } from '../chatStore';
import { TableChatBadge } from '../TableChatBadge';
import { TableChatPanel } from '../TableChatPanel';
import { FakeChatServer, message } from './fakeChatServer';

/**
 * Table chat (TC): axe without blocking violations in both themes — the panel with messages (own, foreign, edited,
 * deleted, unread, with a mention), the open composer with the mention list, the delete confirmation, the read-only
 * state, and the badge in a table title. Texts are the real catalog: `⟦key⟧` on screen fails the test.
 *
 * ⚠ jsdom has no layout, so `color-contrast` is not computed here; the colours are Mantine/kit theme variables
 * only (`tableChat.css`), and the contrast of the real colours is checked by the e2e spec `tableChat.spec.ts`
 * (axe in a real browser, light and dark).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { userId: 1, permissions: ['Document.View', 'Document.Discuss'] } }),
}));

const Table = {
  allowsDynamicRows: false,
  maxDynamicRows: null,
  sheetCode: 'A',
  sheetDefId: 1,
  sheetNameL10n: { values: { en: 'Sheet A' } },
  sheetOrdinal: 0,
  tableCode: 'T1',
  tableDefId: 1,
  tableInstanceId: 10,
  tableNameL10n: { values: { en: '3.00 Stationary Equipment' } },
  tableOrdinal: 0,
} as DocumentTableDto;

const server = new FakeChatServer();

afterEach(() => {
  cleanup();
  act(() => publishSummary(7, null));
  vi.unstubAllGlobals();
});

function mount(scheme: 'light' | 'dark'): HTMLElement {
  const { container } = render(
    <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <main>
            <TableChatBadge documentId={7} tableCode="T1" />
            <TableChatPanel documentId={7} periodKey={202606} table={Table} onClose={() => undefined} takeFocus={false} />
          </main>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return container;
}

async function expectClean(root: HTMLElement): Promise<void> {
  expect(root.textContent).not.toContain('⟦');
  const violations = await findViolations(root);

  expect(violations, describeViolations(violations)).toHaveLength(0);
}

describe('Table chat panel — axe', () => {
  it.each(Themes)('%s: messages, composer with the mention list, delete confirmation, badge', async (scheme) => {
    server.messages = [
      message(1, { body: 'first' }),
      message(2, {
        authorName: 'Aigerim Sadykova',
        authorUserId: 3,
        body: 'ping @Aidar Nurlanov',
        mentions: [{ userId: 2, name: 'Aidar Nurlanov' }],
        editedAt: '2026-06-01T11:00:00Z',
      }),
      message(3, { body: null, isDeleted: true, deletedAt: '2026-06-01T12:00:00Z' }),
      message(4, {
        authorUserId: 1,
        authorName: 'Me',
        canEdit: true,
        editableUntil: new Date(Date.now() + 600_000).toISOString(),
        canDelete: true,
      }),
    ];
    server.candidates = [{ userId: 7, name: 'Aigerim Sadykova' }];
    server.install();
    act(() =>
      publishSummary(7, [{ tableCode: 'T1', total: 4, unread: 2, lastAt: null, lastMessageId: 4, mentionedMe: false, revision: 1 }]),
    );

    const container = mount(scheme);
    await screen.findByText('first');
    await expectClean(container);

    // The composer with an open mention list.
    fireEvent.change(await screen.findByRole('textbox', { name: /Message/ }), { target: { value: 'hi @Ai' } });
    await screen.findByRole('listbox', {}, { timeout: 3000 });
    await expectClean(container);

    // The delete confirmation of an own message.
    fireEvent.click(document.querySelector('[data-chat-delete]') as HTMLElement);
    await expectClean(container);
  });

  it.each(Themes)('%s: read-only and empty states', async (scheme) => {
    server.messages = [];
    server.canWrite = false;
    server.install();

    const container = mount(scheme);
    await screen.findByText(Catalog['tableChat.readOnly'] ?? '');
    await expectClean(container);
    server.canWrite = true;
  });
});
