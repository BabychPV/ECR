import type { DiscussionMention, DiscussionMessage } from './types';

/**
 * Pure rules of the table chat (no React, no network): merging a window of messages, the `@` trigger of the
 * composer, which mentions survive in the text, how a body is split for rendering.
 */

/** Replaces messages with the same id, adds new ones; the result is ascending by id (the key of pagination). */
export function mergeMessages(
  current: readonly DiscussionMessage[],
  incoming: readonly DiscussionMessage[],
): DiscussionMessage[] {
  const byId = new Map<number, DiscussionMessage>();
  for (const message of current) byId.set(message.id, message);
  for (const message of incoming) byId.set(message.id, message);

  return [...byId.values()].sort((a, b) => a.id - b.id);
}

/**
 * Can the edit button be shown right now? The server decides (`canEdit`); the client only stops offering the edit
 * once `editableUntil` has passed, so the user does not type into a form the server will refuse (`422`).
 */
export function canEditNow(message: DiscussionMessage, nowMs: number): boolean {
  if (!message.canEdit || message.isDeleted || message.editableUntil === null) return false;

  return Date.parse(message.editableUntil) > nowMs;
}

/** A message by someone else, written after the read mark that was current when the panel opened. */
export function isUnread(message: DiscussionMessage, readBaseline: number, currentUserId: number | undefined): boolean {
  return !message.isDeleted && message.id > readBaseline && message.authorUserId !== currentUserId;
}

/** Two initials for the avatar: "Aigerim Sadykova" → "AS", "land-owner" → "LO". */
export function initialsOf(name: string): string {
  const parts = name
    .trim()
    .split(/[\s._-]+/)
    .filter((part) => part.length > 0);
  const letters = parts.length >= 2 ? [parts[0], parts[1]] : [parts[0]?.slice(0, 2)];

  return letters
    .map((part) => (part === undefined ? '' : (Array.from(part)[0] ?? '')))
    .join('')
    .toUpperCase();
}

/** Longest `@query` the trigger accepts (a full name with spaces still fits). */
const MaxTriggerLength = 40;

export interface MentionTrigger {
  /** Index of the `@`. */
  readonly start: number;
  /** Text typed after the `@` up to the caret. */
  readonly query: string;
}

/**
 * The `@` the caret is currently inside: the `@` starts the text or follows whitespace, there is no line break
 * between it and the caret, and the query is short. `null` — the caret is not in a mention.
 */
export function mentionTrigger(text: string, caret: number): MentionTrigger | null {
  const upto = text.slice(0, caret);
  const at = upto.lastIndexOf('@');
  if (at < 0) return null;
  if (at > 0 && !/\s/.test(upto.charAt(at - 1))) return null;

  const query = upto.slice(at + 1);
  if (query.length > MaxTriggerLength || query.includes('\n')) return null;

  return { start: at, query };
}

/** Puts `@Name ` instead of `@query` and returns the new text with the new caret position. */
export function applyMention(
  text: string,
  trigger: MentionTrigger,
  caret: number,
  name: string,
): { readonly text: string; readonly caret: number } {
  const inserted = `@${name} `;
  const next = `${text.slice(0, trigger.start)}${inserted}${text.slice(caret)}`;

  return { text: next, caret: trigger.start + inserted.length };
}

/** Is `@name` in the text as a whole mention (not a prefix of a longer word)? */
export function containsMention(body: string, name: string): boolean {
  return findMention(body, name, 0) >= 0;
}

function isWordChar(char: string): boolean {
  return /[\p{L}\p{N}_]/u.test(char);
}

function findMention(body: string, name: string, from: number): number {
  const needle = `@${name}`;
  let index = body.indexOf(needle, from);

  while (index >= 0) {
    const before = index === 0 ? '' : body.charAt(index - 1);
    const after = body.charAt(index + needle.length);
    if ((before === '' || !isWordChar(before)) && (after === '' || !isWordChar(after))) return index;
    index = body.indexOf(needle, index + 1);
  }

  return -1;
}

/**
 * Ids sent as `mentionUserIds`: only people whose `@Name` is still in the text (the composer remembers every
 * pick, but the user may have deleted the name afterwards). At most `limit` (the server takes up to 10).
 */
export function liveMentionIds(
  body: string,
  picked: ReadonlyMap<number, string>,
  limit = 10,
): number[] {
  const ids: number[] = [];
  for (const [userId, name] of picked) {
    if (containsMention(body, name)) ids.push(userId);
  }

  return ids.slice(0, limit);
}

export interface BodySegment {
  readonly text: string;
  readonly mention: boolean;
}

/**
 * Splits a body for rendering: the mentions the server confirmed are highlighted, everything else is plain text.
 * ⛔ Never HTML: the pieces go into React text nodes (XSS-safe), links are not made clickable.
 */
export function splitBody(body: string, mentions: readonly DiscussionMention[]): BodySegment[] {
  const marks: { start: number; end: number }[] = [];

  // Longest names first so "@Ann Lee" wins over "@Ann".
  for (const mention of [...mentions].sort((a, b) => b.name.length - a.name.length)) {
    let from = 0;
    for (;;) {
      const index = findMention(body, mention.name, from);
      if (index < 0) break;
      const end = index + mention.name.length + 1;
      if (!marks.some((mark) => index < mark.end && end > mark.start)) marks.push({ start: index, end });
      from = end;
    }
  }

  marks.sort((a, b) => a.start - b.start);

  const segments: BodySegment[] = [];
  let cursor = 0;
  for (const mark of marks) {
    if (mark.start > cursor) segments.push({ text: body.slice(cursor, mark.start), mention: false });
    segments.push({ text: body.slice(mark.start, mark.end), mention: true });
    cursor = mark.end;
  }
  if (cursor < body.length) segments.push({ text: body.slice(cursor), mention: false });

  return segments;
}

/** A fresh GUID for one compose action (reused on retry so a lost answer does not create a second message). */
export function newClientMessageId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();

  // Insecure context (plain HTTP on a stand) has no randomUUID: RFC 4122 v4 from getRandomValues/Math.random.
  const bytes = new Uint8Array(16);
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') crypto.getRandomValues(bytes);
  else for (let i = 0; i < bytes.length; i += 1) bytes[i] = Math.floor(Math.random() * 256);

  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');

  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
