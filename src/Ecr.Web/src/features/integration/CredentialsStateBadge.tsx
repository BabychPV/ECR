import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { t } from '@/shared/i18n';
import { badgeLook } from '@/shared/ui/StatusBadge';
import type { DataSource } from './dataSourceApi';

/** Ключ короткого підпису бейджа. */
function badgeKeyOf(source: Pick<DataSource, 'credentialsState'>): string {
  return source.credentialsState === 'Rejected' ? 'sources.credentialsRejectedBadge' : 'sources.credentialsUnreadableBadge';
}

/** Ключ полного тексту стану; `null` — облікові дані в порядку. */
export function credentialsStateKeyOf(source: Pick<DataSource, 'credentialsState'>): string | null {
  if (source.credentialsState === 'Rejected') return 'sources.credentialsRejected';
  if (source.credentialsState === 'Unreadable') return 'sources.credentialsUnreadable';

  return null;
}

/**
 * Бейдж «вхід відхилено / пароль не читається» (P-1126-SRCCRED). Кольори — лише токени `badgeLook` через
 * властивості Mantine. Повний текст — у `title` і в підписі для читалки; видимий підпис короткий.
 * `null`, коли стан у порядку.
 */
export function CredentialsStateBadge({ source }: { readonly source: DataSource }): JSX.Element | null {
  const key = credentialsStateKeyOf(source);
  if (key === null) return null;

  const look = badgeLook(source.credentialsState === 'Rejected' ? 'danger' : 'warning');

  return (
    <Badge
      size="sm"
      variant="transparent"
      miw="fit-content"
      radius="xs"
      c={look.text}
      bg={look.bg}
      bd={`1px solid ${look.border}`}
      title={t(key)}
      aria-label={`${t(badgeKeyOf(source))}: ${t(key)}`}
      data-credentials-state-badge={source.credentialsState}
    >
      {t(badgeKeyOf(source))}
    </Badge>
  );
}
