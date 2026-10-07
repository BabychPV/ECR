import type { JSX } from 'react';
import { Stack, Text, Title } from '@mantine/core';
import type { ConsistencyIssue } from '@/api/types';
import { t } from '@/shared/i18n';
import { KeyValue } from '@/shared/ui/KeyValue';
import { Timestamp } from '@/shared/ui/Timestamp';

/**
 * Вміст шторки знахідки (UI-20; макет `screens-ops.js` `/admin/consistency`,
 * `drawer`).
 *
 * ⚠ ЛІНИВИЙ чанк: шторка закрита за замовчуванням (`L2`).
 *
 * ⛔ Лише те, що віддає `ConsistencyIssueView`. Розділів макета «What this
 * means» / «What to do», «Where» (документ/аркуш/клітинка), «Last seen» і
 * «Found by» тут НЕМАЄ: даних під них сервер не віддає (`D15-06`, TODO-контракт
 * у листі готовності), а вигаданий текст гірший за відсутній.
 */
export default function ConsistencyIssueDetail({ issue }: { readonly issue: ConsistencyIssue }): JSX.Element {
  return (
    <Stack gap="md">
      <Stack gap="xs">
        <Title order={3} size="h5">
          {t('consistency.what')}
        </Title>
        <Text size="sm">{issue.message}</Text>
        <Text size="xs" c="dimmed">
          {t('consistency.messageLanguage')}
        </Text>
      </Stack>

      <div data-allow-dotted>
        <KeyValue
          items={[
            { label: t('consistency.rule'), value: issue.ruleCode, mono: true },
            {
              label: t('consistency.entity'),
              value:
                issue.entityType === null
                  ? ''
                  : `${issue.entityType}${issue.entityId === null ? '' : ` · ${String(issue.entityId)}`}`,
              mono: true,
            },
            { label: t('consistency.when'), value: <Timestamp value={issue.detectedAt} /> },
            ...(issue.resolvedAt === null
              ? []
              : [{ label: t('consistency.resolved'), value: <Timestamp value={issue.resolvedAt} /> }]),
          ]}
        />
      </div>
    </Stack>
  );
}
