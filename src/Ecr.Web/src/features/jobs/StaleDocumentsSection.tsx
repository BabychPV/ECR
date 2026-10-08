import type { JSX } from 'react';
import { Anchor, Card, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { staleDocumentHref } from './myTasks';
import { useStaleDocuments } from './useStaleDocuments';

/**
 * Блок «Потребують перерахунку» у шухляді «My tasks».
 *
 * Мета (рішення людини): не натиснув «Перерахувати» - система пам'ятає й нагадує. Автоперерахунку немає, тож
 * перелік тут - єдине місце, де лишається слід. Порожній і завантажувальний стани мовчать (блок не заважає
 * переліку задач); відмова - видима, а не німа (`L10`).
 */
export function StaleDocumentsSection({ opened }: { readonly opened: boolean }): JSX.Element | null {
  const stale = useStaleDocuments(opened);

  if (stale.error !== null) {
    return (
      <Text size="sm" c="dimmed" mb="md" data-testid="my-tasks-stale-error">
        {t('jobs.staleDocuments.error')}{' '}
        <Anchor component="button" type="button" size="sm" onClick={stale.refetch}>
          {t('common.retry')}
        </Anchor>
      </Text>
    );
  }
  if (stale.items.length === 0) return null;

  return (
    <Stack gap="xs" mb="md" component="section" aria-labelledby="my-tasks-stale-title" data-testid="my-tasks-stale">
      <Text id="my-tasks-stale-title" fw={600} size="sm">
        {t('jobs.staleDocuments.title')}
      </Text>
      <Text size="xs" c="dimmed">
        {t('jobs.staleDocuments.hint')}
      </Text>
      {stale.items.map(({ document, periodKey }) => (
        <Card withBorder padding="xs" key={`${String(document.id)}:${String(periodKey)}`} data-stale-document={document.id}>
          <Group justify="space-between" wrap="nowrap" gap="xs">
            <div>
              <Text size="sm" fw={500}>
                {document.businessKey}
              </Text>
              {document.resultsStaleSince != null && (
                <Text size="xs" c="dimmed">
                  {t('jobs.staleDocuments.since', { date: formatDateTime(document.resultsStaleSince) })}
                </Text>
              )}
            </div>
            <Anchor component={Link} to={staleDocumentHref(document.id, periodKey)} size="sm">
              {t('jobs.staleDocuments.open')}
            </Anchor>
          </Group>
        </Card>
      ))}
    </Stack>
  );
}
