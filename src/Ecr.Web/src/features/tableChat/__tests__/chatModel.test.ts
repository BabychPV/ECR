import { describe, expect, it } from 'vitest';
import {
  applyMention,
  canEditNow,
  containsMention,
  initialsOf,
  isUnread,
  liveMentionIds,
  mentionTrigger,
  mergeMessages,
  newClientMessageId,
  splitBody,
} from '../chatModel';
import type { DiscussionMessage } from '../types';

function message(overrides: Partial<DiscussionMessage> & { id: number }): DiscussionMessage {
  return {
    tableCode: 'T1',
    periodKey: 202606,
    authorUserId: 1,
    authorName: 'Ann Lee',
    body: 'text',
    isDeleted: false,
    createdAt: '2026-06-01T10:00:00Z',
    editedAt: null,
    deletedAt: null,
    canEdit: false,
    editableUntil: null,
    canDelete: false,
    mentions: [],
    ...overrides,
  };
}

describe('mergeMessages', () => {
  it('replaces by id, adds new ones, keeps ascending order', () => {
    const merged = mergeMessages(
      [message({ id: 3, body: 'old' }), message({ id: 1 })],
      [message({ id: 3, body: 'edited' }), message({ id: 2 })],
    );

    expect(merged.map((m) => m.id)).toEqual([1, 2, 3]);
    expect(merged[2]?.body).toBe('edited');
  });
});

describe('canEditNow', () => {
  const until = '2026-06-01T10:15:00Z';

  it('is open inside the window and only when the server said canEdit', () => {
    const base = message({ id: 1, canEdit: true, editableUntil: until });

    expect(canEditNow(base, Date.parse('2026-06-01T10:14:59Z'))).toBe(true);
    expect(canEditNow(base, Date.parse('2026-06-01T10:15:01Z'))).toBe(false);
    expect(canEditNow({ ...base, canEdit: false }, Date.parse('2026-06-01T10:00:00Z'))).toBe(false);
    expect(canEditNow({ ...base, editableUntil: null }, Date.parse('2026-06-01T10:00:00Z'))).toBe(false);
    expect(canEditNow({ ...base, isDeleted: true }, Date.parse('2026-06-01T10:00:00Z'))).toBe(false);
  });
});

describe('isUnread', () => {
  it('counts only other people\'s live messages after the baseline', () => {
    expect(isUnread(message({ id: 5, authorUserId: 2 }), 4, 1)).toBe(true);
    expect(isUnread(message({ id: 4, authorUserId: 2 }), 4, 1)).toBe(false);
    expect(isUnread(message({ id: 5, authorUserId: 1 }), 4, 1)).toBe(false);
    expect(isUnread(message({ id: 5, authorUserId: 2, isDeleted: true, body: null }), 4, 1)).toBe(false);
  });
});

describe('initialsOf', () => {
  it('takes two initials of a name and of a login', () => {
    expect(initialsOf('Aigerim Sadykova')).toBe('AS');
    expect(initialsOf('land-owner')).toBe('LO');
    expect(initialsOf('admin')).toBe('A');
    expect(initialsOf('  ')).toBe('');
  });
});

describe('mentionTrigger', () => {
  it('finds an @ that starts the text or follows whitespace', () => {
    expect(mentionTrigger('@Ai', 3)).toEqual({ start: 0, query: 'Ai' });
    expect(mentionTrigger('hello @Ai', 9)).toEqual({ start: 6, query: 'Ai' });
    expect(mentionTrigger('hello\n@Ai', 9)).toEqual({ start: 6, query: 'Ai' });
  });

  it('ignores an @ inside a word (e-mail) and a query that crossed a line break', () => {
    expect(mentionTrigger('a@b', 3)).toBeNull();
    expect(mentionTrigger('@Ai\nnext', 8)).toBeNull();
    expect(mentionTrigger('no at here', 5)).toBeNull();
  });

  it('gives up on a very long query', () => {
    expect(mentionTrigger(`@${'x'.repeat(60)}`, 61)).toBeNull();
  });
});

describe('applyMention', () => {
  it('replaces @query with @Name and a space and moves the caret behind it', () => {
    const trigger = mentionTrigger('Checked with @Ai now', 16);
    expect(trigger).not.toBeNull();
    if (trigger === null) return;

    const next = applyMention('Checked with @Ai now', trigger, 16, 'Aigerim Sadykova');

    expect(next.text).toBe('Checked with @Aigerim Sadykova  now');
    expect(next.caret).toBe('Checked with @Aigerim Sadykova '.length);
  });
});

describe('liveMentionIds', () => {
  it('keeps only people whose @Name is still in the text', () => {
    const picked = new Map([
      [7, 'Aigerim Sadykova'],
      [9, 'Aidar Nurlanov'],
    ]);

    expect(liveMentionIds('please @Aigerim Sadykova check', picked)).toEqual([7]);
    expect(liveMentionIds('nobody here', picked)).toEqual([]);
  });

  it('does not take a prefix of a longer word for a mention', () => {
    expect(containsMention('@Ann Leeds wrote', 'Ann Lee')).toBe(false);
    expect(containsMention('@Ann Lee, hi', 'Ann Lee')).toBe(true);
  });

  it('is limited to ten people', () => {
    const picked = new Map(Array.from({ length: 12 }, (_, i) => [i + 1, `P${String(i + 1)}x`] as const));
    const body = [...picked.values()].map((name) => `@${name}`).join(' ');

    expect(liveMentionIds(body, picked)).toHaveLength(10);
  });
});

describe('splitBody', () => {
  it('highlights confirmed mentions and leaves the rest as text (never HTML)', () => {
    const segments = splitBody('OK <b>x</b> @Aidar Nurlanov please', [{ userId: 9, name: 'Aidar Nurlanov' }]);

    expect(segments).toEqual([
      { text: 'OK <b>x</b> ', mention: false },
      { text: '@Aidar Nurlanov', mention: true },
      { text: ' please', mention: false },
    ]);
  });

  it('prefers the longer name when two mentions share a prefix', () => {
    const segments = splitBody('@Ann Lee and @Ann', [
      { userId: 1, name: 'Ann' },
      { userId: 2, name: 'Ann Lee' },
    ]);

    expect(segments.filter((s) => s.mention).map((s) => s.text)).toEqual(['@Ann Lee', '@Ann']);
  });

  it('returns one plain segment when there are no mentions', () => {
    expect(splitBody('plain', [])).toEqual([{ text: 'plain', mention: false }]);
  });
});

describe('newClientMessageId', () => {
  it('is a GUID and differs per call', () => {
    const a = newClientMessageId();
    const b = newClientMessageId();

    expect(a).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(a).not.toBe(b);
  });
});
