import type { JSX } from 'react';
import { ActionIcon, Button, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import './notices.css';

export interface NoticeAction {
  readonly label: string;
  readonly run: () => void;
  readonly testId?: string;
  /** Клавіша дії для читалки (`aria-keyshortcuts`), напр. `F8`. */
  readonly keys?: string;
}

interface NoticeRowProps {
  readonly tone: 'error' | 'warning' | 'info';
  readonly text: string;
  readonly actions?: readonly NoticeAction[];
  readonly onDismiss?: () => void;
  readonly testId?: string;
}

/**
 * ✎ DOCUX L7: ОДИН рядок повідомлення (32 px): знак тону, текст (обрізається, повністю — у підказці), головна дія,
 * хрестик, якщо повідомлення можна закрити. Колір — лише разом із знаком і словом (`!` / `i`).
 */
export function NoticeRow({ tone, text, actions = [], onDismiss, testId }: NoticeRowProps): JSX.Element {
  return (
    <div className="ecr-notice-row" data-tone={tone} data-testid={testId} role={tone === 'error' ? 'alert' : 'status'}>
      <span className="ecr-notice-icon" aria-hidden="true">
        {tone === 'info' ? 'i' : '!'}
      </span>
      <Text size="sm" className="ecr-notice-text" title={text}>
        {text}
      </Text>
      {actions.map((action) => (
        <Button
          key={action.label}
          size="xs"
          variant="subtle"
          onClick={action.run}
          data-testid={action.testId}
          aria-keyshortcuts={action.keys}
        >
          {action.label}
        </Button>
      ))}
      {onDismiss !== undefined && (
        <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t('common.close')} title={t('common.close')} onClick={onDismiss}>
          ✕
        </ActionIcon>
      )}
    </div>
  );
}
