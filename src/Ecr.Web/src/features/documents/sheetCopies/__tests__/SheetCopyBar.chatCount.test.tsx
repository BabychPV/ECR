import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { SheetCopyBar, type SheetCopyTarget } from '../SheetCopyBar';
import { chatMessageCountOf } from '../sheetGroups';

/**
 * TC02: deleting a copy removes its chat, so the dialog says how many messages go with it BEFORE the confirmation
 * (`DocumentTableDto.chatMessageCount`). Texts are the real seeded catalog.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

const Copy: SheetCopyTarget = {
  sheetDefId: 10,
  sheetCode: 'GEN',
  label: 'Boiler 2',
  groupNameValues: { en: 'Boiler' },
  sheetCopyId: 77,
  isCopyable: true,
};

async function openDeleteDialog(target: SheetCopyTarget): Promise<string> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <SheetCopyBar documentId={42} target={target} canAdd onSelect={vi.fn()} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  fireEvent.click(screen.getByTestId('sheet-copy-delete'));

  return (await screen.findByRole('dialog')).textContent ?? '';
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('delete-copy dialog: chat messages that go with the copy', () => {
  it('shows the count when the copy has chat messages', async () => {
    const text = await openDeleteDialog({ ...Copy, chatMessageCount: 5 });

    expect(Catalog['documents.sheetCopies.deleteChat']).toBeDefined();
    expect(text).toContain((Catalog['documents.sheetCopies.deleteChat'] ?? '').replace('{n}', '5'));
  });

  it.each([0, null, undefined])('shows nothing about the chat when the count is %s', async (count) => {
    const text = await openDeleteDialog(count === undefined ? Copy : { ...Copy, chatMessageCount: count });

    expect(text).not.toContain((Catalog['documents.sheetCopies.deleteChat'] ?? '⟦missing⟧').replace('{n}', '').slice(0, 20));
    expect(text).toContain('Boiler 2');
  });
});

describe('chatMessageCountOf', () => {
  it('takes the first table of the copy that has a count; originals (null/absent) give null', () => {
    const table = (chatMessageCount: number | null | undefined) => ({ chatMessageCount }) as never;

    expect(chatMessageCountOf({ tables: [table(null), table(4), table(4)] })).toBe(4);
    expect(chatMessageCountOf({ tables: [table(0)] })).toBe(0);
    expect(chatMessageCountOf({ tables: [table(null), table(undefined)] })).toBeNull();
  });
});
