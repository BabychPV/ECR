import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render } from '@testing-library/react';
import { chatTableOverride, publishSummary, setChatTable } from '../chatStore';
import { TableChatBadge } from '../TableChatBadge';

/**
 * The badge lives in the grid chunk (`SheetTables`), whose tests mount it WITHOUT a router and a `QueryClient`.
 * It must render (or stay empty) in that environment and must not need either: it reads a store and only asks the
 * host to open the panel.
 */
afterEach(() => {
  cleanup();
  act(() => {
    publishSummary(7, null);
    setChatTable(null);
  });
});

describe('TableChatBadge without a router and a QueryClient', () => {
  it('renders nothing when no summary was published', () => {
    const { container } = render(<TableChatBadge documentId={7} tableCode="T1" />);

    expect(container.innerHTML).toBe('');
  });

  it('renders the counter and a click records the explicit table (the host writes the address)', () => {
    act(() =>
      publishSummary(7, [{ tableCode: 'T1', total: 3, unread: 1, lastAt: null, lastMessageId: 3, mentionedMe: false }]),
    );
    const { container } = render(<TableChatBadge documentId={7} tableCode="T1" />);

    fireEvent.click(container.querySelector('[data-chat-badge]') as HTMLElement);

    expect(chatTableOverride()).toBe('T1');
  });
});
