import type { JSX } from 'react';
import { Anchor, Card, Group, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { staleDocumentHref } from './myTasks';
import { MyTasksSection } from './MyTasksSection';
import { useStaleDocuments } from './useStaleDocuments';

/**
 * Блок «Потребують перерахунку» у шухляді «My tasks».
 *
 * Мета (рішення людини): не натиснув «Перерахувати» - система пам'ятає й нагадує. Автоперерахунку немає, тож
 * перелік тут - єдине місце, де лишається слід. Порожній і завантажувальний стани мовчать (блок не заважає
 * переліку задач); відмова - видима, а не німа (`L10`).
 */
export function StaleDocumentsSection({
  opened,
  onNavigate,
}: {
  readonly opened: boolean;
  /** Перехід на документ: шухляда закривається, інакше лишається поверх нього. */
  readonly onNavigate?: (() => void) | undefined;
}): JSX.Element | null {
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
    <MyTasksSection id="my-tasks-stale" title={t('jobs.staleDocuments.title')} count={stale.items.length}>
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
            <Anchor component={Link} to={staleDocumentHref(document.id, periodKey)} size="sm" onClick={onNavigate}>
              {t('jobs.staleDocuments.open')}
            </Anchor>
          </Group>
        </Card>
      ))}
    </MyTasksSection>
  );
}
