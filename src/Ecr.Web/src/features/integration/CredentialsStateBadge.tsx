import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { t } from '@/shared/i18n';
import { badgeLook } from '@/shared/ui/StatusBadge';
import type { DataSource } from './dataSourceApi';

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

  const rejected = source.credentialsState === 'Rejected';
  const look = badgeLook(rejected ? 'danger' : 'warning');
  // ⚠ Ключі — умовні вирази над ЛІТЕРАЛАМИ: сторож каталогу (EndpointCoverageTests) бачить їх із тексту.
  const full = t(rejected ? 'sources.credentialsRejected' : 'sources.credentialsUnreadable');
  const short = t(rejected ? 'sources.credentialsRejectedBadge' : 'sources.credentialsUnreadableBadge');

  return (
    <Badge
      size="sm"
      variant="transparent"
      miw="fit-content"
      radius="xs"
      c={look.text}
      bg={look.bg}
      bd={`1px solid ${look.border}`}
      title={full}
      aria-label={`${short}: ${full}`}
      data-credentials-state-badge={source.credentialsState}
    >
      {short}
    </Badge>
  );
}
