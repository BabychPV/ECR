import { useState, type JSX } from 'react';
import { Button, Group, Text, Textarea } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { Timestamp } from '@/shared/ui/Timestamp';
import { MaxBodyLength } from './api';
import { canEditNow, containsMention, initialsOf, splitBody } from './chatModel';
import type { DiscussionMessage } from './types';

export interface ChatMessageItemProps {
  readonly message: DiscussionMessage;
  /** Wall clock for the edit window; the panel re-renders it periodically. */
  readonly nowMs: number;
  /** Written by someone else after the read mark the panel opened with. */
  readonly unread: boolean;
  /** Saves the edit; rejects when the server refuses (the caller has shown the reason). */
  readonly onSave: (message: DiscussionMessage, body: string, mentionUserIds: number[]) => Promise<void>;
  /** Soft-deletes; rejects when the server refuses (the caller has shown the reason). */
  readonly onRemove: (message: DiscussionMessage) => Promise<void>;
}

/**
 * One message: author, time, period tag, "edited"/"new" marks, plain text with the confirmed mentions highlighted.
 *
 * ⛔ The body is rendered as React text nodes only (never HTML) and links are not made clickable. A deleted message
 * has no body at all (`body = null`): "Message deleted".
 *
 * ⚠ Edit and delete are offered only when the server said so (`canEdit`/`canDelete`); the edit is also withdrawn
 * once `editableUntil` has passed. Delete asks for a second click in place (no modal inside a non-modal panel).
 */
export function ChatMessageItem({ message, nowMs, unread, onSave, onRemove }: ChatMessageItemProps): JSX.Element {
  const [mode, setMode] = useState<'view' | 'edit' | 'confirm'>('view');
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const loading = usePendingLoading(busy);

  const editable = canEditNow(message, nowMs);
  const deletable = message.canDelete && !message.isDeleted;

  const save = async (): Promise<void> => {
    const body = draft.trim();
    if (busy || body.length === 0 || body.length > MaxBodyLength) return;

    setBusy(true);
    try {
      // Mentions that are still in the text stay; new ones are made in the composer, not while editing.
      const keep = message.mentions.filter((mention) => containsMention(body, mention.name)).map((mention) => mention.userId);
      await onSave(message, body, keep);
      setMode('view');
    } catch {
      // The reason is on screen; the draft stays.
    } finally {
      setBusy(false);
    }
  };

  const remove = async (): Promise<void> => {
    if (busy) return;

    setBusy(true);
    try {
      await onRemove(message);
      setMode('view');
    } catch {
      // The reason is on screen.
    } finally {
      setBusy(false);
    }
  };

  const segments = message.body === null ? [] : splitBody(message.body, message.mentions);

  return (
    <li className="ecr-chat-msg" data-chat-message={message.id} data-chat-unread={unread ? '' : undefined}>
      <span className="ecr-chat-avatar" aria-hidden="true">
        {initialsOf(message.authorName)}
      </span>

      <div className="ecr-chat-msg-main">
        <div className="ecr-chat-msg-head">
          <Text span fw={600} size="sm" data-chat-author="">
            {message.authorName}
          </Text>
          <Text span size="xs" c="dimmed">
            <Timestamp value={message.createdAt} />
          </Text>
          <span className="ecr-chat-tag" data-chat-period="">
            {formatPeriodKey(message.periodKey)}
          </span>
          {message.editedAt !== null && !message.isDeleted && (
            <Text span size="xs" c="dimmed" data-chat-edited="">
              {t('tableChat.edited')}
            </Text>
          )}
          {unread && (
            <Text span size="xs" fw={600} className="ecr-chat-new" data-chat-new="">
              {t('tableChat.new')}
            </Text>
          )}
        </div>

        {message.isDeleted && (
          <Text size="sm" c="dimmed" fs="italic" data-chat-deleted="">
            {t('tableChat.deleted')}
          </Text>
        )}

        {!message.isDeleted && mode !== 'edit' && (
          <div className="ecr-chat-body" data-chat-body="">
            {segments.map((segment, index) =>
              segment.mention ? (
                <span key={index} className="ecr-chat-mention-text" data-chat-mentioned="">
                  {segment.text}
                </span>
              ) : (
                <span key={index}>{segment.text}</span>
              ),
            )}
          </div>
        )}

        {mode === 'edit' && (
          <div className="ecr-chat-edit">
            <Textarea
              label={t('tableChat.editLabel')}
              value={draft}
              autosize
              minRows={2}
              maxRows={8}
              error={draft.length > MaxBodyLength ? t('tableChat.tooLong', { max: MaxBodyLength }) : undefined}
              onChange={(event) => setDraft(event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                  event.preventDefault();
                  void save();
                }
                if (event.key === 'Escape') {
                  event.preventDefault();
                  event.stopPropagation();
                  setMode('view');
                }
              }}
            />
            <Group gap="xs" mt="xs">
              <Button
                size="compact-xs"
                loading={loading}
                disabled={draft.trim().length === 0 || draft.length > MaxBodyLength}
                onClick={() => void save()}
                data-chat-save=""
              >
                {t('common.save')}
              </Button>
              <Button size="compact-xs" variant="default" onClick={() => setMode('view')}>
                {t('common.cancel')}
              </Button>
            </Group>
          </div>
        )}

        {mode === 'view' && (editable || deletable) && (
          <Group gap="xs" className="ecr-chat-actions">
            {editable && (
              <Button
                size="compact-xs"
                variant="subtle"
                color="gray"
                c="dimmed"
                onClick={() => {
                  setDraft(message.body ?? '');
                  setMode('edit');
                }}
                data-chat-edit=""
              >
                {t('tableChat.edit')}
              </Button>
            )}
            {deletable && (
              <Button
                size="compact-xs"
                variant="subtle"
                color="statusError"
                onClick={() => setMode('confirm')}
                data-chat-delete=""
              >
                {t('tableChat.delete')}
              </Button>
            )}
          </Group>
        )}

        {mode === 'confirm' && (
          <Group gap="xs" className="ecr-chat-actions" role="group" aria-label={t('tableChat.deleteConfirm')}>
            <Text size="xs">{t('tableChat.deleteConfirm')}</Text>
            <Button size="compact-xs" color="statusError" loading={loading} onClick={() => void remove()} data-chat-delete-yes="">
              {t('tableChat.delete')}
            </Button>
            <Button size="compact-xs" variant="default" onClick={() => setMode('view')}>
              {t('common.cancel')}
            </Button>
          </Group>
        )}
      </div>
    </li>
  );
}
