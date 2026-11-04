import { useEffect, useId, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { Button, Text, Textarea } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { MaxBodyLength, MinMentionQuery, useMentionCandidates } from './api';
import { applyMention, liveMentionIds, mentionTrigger, newClientMessageId } from './chatModel';
import type { MentionCandidate, PostMessageRequest } from './types';

export interface ChatComposerProps {
  readonly documentId: number;
  readonly tableCode: string;
  /** Period the message is written in (the document period). */
  readonly periodKey: number;
  readonly periodText: string;
  /** Sends the message; rejects when the server refuses (the caller has already shown the reason). */
  readonly onSubmit: (request: PostMessageRequest) => Promise<void>;
  readonly pending: boolean;
  /** The panel opened by the button: the composer takes the focus. */
  readonly autoFocus?: boolean;
}

/**
 * The composer: plain text, `Ctrl+Enter` sends, `@` opens the mention list fed by the server (only people who can
 * see the table).
 *
 * ⚠ A new GUID per compose action, REUSED on retry: if the answer to a post is lost, sending the same text again
 * carries the same `clientMessageId` and the server returns the existing message instead of creating a second one.
 * Another text is another compose action — another GUID.
 *
 * ⛔ The textarea is the `textbox` role with `aria-autocomplete`/`aria-controls`/`aria-activedescendant` (what the
 * role allows); `role="combobox"` on a `textarea` is not allowed by ARIA-in-HTML and axe would flag it.
 */
export function ChatComposer({
  documentId,
  tableCode,
  periodKey,
  periodText,
  onSubmit,
  pending,
  autoFocus = false,
}: ChatComposerProps): JSX.Element {
  const [text, setText] = useState('');
  const [caret, setCaret] = useState(0);
  const [picked, setPicked] = useState<ReadonlyMap<number, string>>(new Map());
  const [active, setActive] = useState(0);
  /** Start of the trigger the user dismissed with Esc: its list stays closed until the trigger changes. */
  const [dismissed, setDismissed] = useState<number | null>(null);
  const area = useRef<HTMLTextAreaElement>(null);
  const attempt = useRef<{ readonly id: string; readonly body: string } | null>(null);
  const listId = useId();

  // Opened by the button (a human action): the composer takes the focus, like the inspector takes it on its tab.
  useEffect(() => {
    if (autoFocus) area.current?.focus();
  }, [autoFocus]);

  const trigger = mentionTrigger(text, caret);
  const [debounced] = useDebouncedValue(trigger?.query ?? '', 200);
  const candidates = useMentionCandidates(
    documentId,
    tableCode,
    periodKey,
    trigger === null ? '' : debounced,
  );

  const suggestions: readonly MentionCandidate[] =
    trigger !== null && dismissed !== trigger.start && trigger.query.trim().length >= MinMentionQuery
      ? (candidates.data ?? [])
      : [];
  const open = suggestions.length > 0;
  const activeIndex = Math.min(active, Math.max(0, suggestions.length - 1));

  const body = text.trim();
  const tooLong = text.length > MaxBodyLength;
  const canSend = body.length > 0 && !tooLong && !pending;
  const loading = usePendingLoading(pending);

  const pick = (candidate: MentionCandidate): void => {
    if (trigger === null) return;

    const next = applyMention(text, trigger, caret, candidate.name);
    setText(next.text);
    setCaret(next.caret);
    setPicked((previous) => new Map(previous).set(candidate.userId, candidate.name));
    setActive(0);

    // The caret moves after React has written the new value into the textarea.
    window.setTimeout(() => {
      area.current?.focus();
      area.current?.setSelectionRange(next.caret, next.caret);
    }, 0);
  };

  const submit = async (): Promise<void> => {
    if (!canSend) return;

    // Same text as the failed attempt → the same GUID (a retry); another text → a new compose action.
    const id = attempt.current?.body === body ? attempt.current.id : newClientMessageId();
    attempt.current = { id, body };

    const ids = liveMentionIds(body, picked);
    try {
      await onSubmit({
        body,
        periodKey,
        clientMessageId: id,
        ...(ids.length > 0 ? { mentionUserIds: ids } : {}),
      });
    } catch {
      // The reason is already on screen; the text and the GUID stay for the retry.
      return;
    }

    attempt.current = null;
    setText('');
    setCaret(0);
    setPicked(new Map());
    setDismissed(null);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>): void => {
    if (open) {
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        const step = event.key === 'ArrowDown' ? 1 : -1;
        setActive((activeIndex + step + suggestions.length) % suggestions.length);
        return;
      }
      if ((event.key === 'Enter' && !event.ctrlKey && !event.metaKey) || event.key === 'Tab') {
        const candidate = suggestions[activeIndex];
        if (candidate !== undefined) {
          event.preventDefault();
          pick(candidate);
          return;
        }
      }
      if (event.key === 'Escape') {
        // Closes the list only; the panel keeps its own Esc.
        event.preventDefault();
        event.stopPropagation();
        setDismissed(trigger?.start ?? null);
        return;
      }
    }

    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void submit();
    }
  };

  const syncCaret = (): void => setCaret(area.current?.selectionStart ?? 0);

  return (
    <div className="ecr-chat-composer">
      {open && (
        <div className="ecr-chat-mentions" role="listbox" id={listId} aria-label={t('tableChat.mentionList')}>
          {suggestions.map((candidate, index) => (
            <div
              key={candidate.userId}
              id={`${listId}-${String(candidate.userId)}`}
              role="option"
              aria-selected={index === activeIndex}
              className="ecr-chat-mention"
              data-chat-mention={candidate.userId}
              // mousedown, not click: the textarea must not lose the caret before the pick.
              onMouseDown={(event) => {
                event.preventDefault();
                pick(candidate);
              }}
            >
              {candidate.name}
            </div>
          ))}
        </div>
      )}

      <Textarea
        ref={area}
        label={t('tableChat.composerLabel', { period: periodText })}
        value={text}
        autosize
        minRows={3}
        maxRows={8}
        error={tooLong ? t('tableChat.tooLong', { max: MaxBodyLength }) : undefined}
        aria-autocomplete="list"
        aria-controls={open ? listId : undefined}
        aria-activedescendant={open ? `${listId}-${String(suggestions[activeIndex]?.userId ?? '')}` : undefined}
        onChange={(event) => {
          setText(event.currentTarget.value);
          setCaret(event.currentTarget.selectionStart);
          setActive(0);
          setDismissed(null);
        }}
        onKeyDown={onKeyDown}
        onKeyUp={syncCaret}
        onClick={syncCaret}
        onSelect={syncCaret}
      />

      <div className="ecr-chat-composer-foot">
        <Text size="xs" c="dimmed">
          {t('tableChat.composerHint', { max: MaxBodyLength })}
        </Text>
        <Button size="xs" disabled={!canSend} loading={loading} onClick={() => void submit()} data-chat-send="">
          {t('tableChat.send')}
        </Button>
      </div>
    </div>
  );
}
