import type { JSX } from 'react';
import { Code, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { queryKeys } from '@/api/queryKeys';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Timestamp } from '@/shared/ui/Timestamp';

/** Одна публікація версії з журналу `aud.PublicationEvent`. */
export type MethodologyPublicationEntry = components['schemas']['MethodologyPublicationEntry'];

/** Журнал публікацій методології — `GET /api/v1/methodologies/{id}/publications`. */
export function methodologyPublications(methodologyId: number): Promise<MethodologyPublicationEntry[]> {
  return apiFetch<MethodologyPublicationEntry[]>(`/api/v1/methodologies/${String(methodologyId)}/publications`);
}

/** Скільки змінених значень золотого набору записано в diff публікації. */
function changedValues(diffJson: string | null | undefined): number | null {
  if (diffJson === null || diffJson === undefined || diffJson.length === 0) {
    return null;
  }

  try {
    const parsed = JSON.parse(diffJson) as { Changes?: unknown[]; changes?: unknown[] };
    const changes = parsed.Changes ?? parsed.changes;

    return Array.isArray(changes) ? changes.length : null;
  } catch {
    return null;
  }
}

/**
 * Журнал публікацій версій методології (F-16, четвертий раунд UX).
 *
 * ⛔ Публікація — найнебезпечніша операція системи (ФВ-9.6), і кожна пише
 * причину та diff РЕЗУЛЬТАТІВ у `aud.PublicationEvent`. Доти цей журнал не мав
 * ні маршруту, ні екрана: «хто, коли й навіщо змінив методологію» можна було
 * дізнатися лише запитом до бази, а автора — лише числом.
 *
 * ⚠ Ключ запиту — під префіксом переліку версій: публікація інвалідує саме
 * його, і журнал оновлюється разом із переліком, без окремого виклику.
 */
export function MethodologyPublicationsPanel({ methodologyId }: { readonly methodologyId: number }): JSX.Element {
  const publications = useQuery({
    queryKey: [...queryKeys.methodologies.versionsOf(methodologyId), 'publications'],
    queryFn: () => methodologyPublications(methodologyId),
  });

  return (
    <Stack gap="xs">
      <Text fw={600}>{t('methodologies.publications')}</Text>

      <AsyncBoundary<MethodologyPublicationEntry[]>
        isPending={publications.isPending}
        error={publications.error}
        data={publications.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.publicationsEmpty')}
        skeleton="table"
        onRetry={() => void publications.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.publishedAt')}</Table.Th>
                <Table.Th>{t('methodologies.version')}</Table.Th>
                <Table.Th>{t('methodologies.publishedBy')}</Table.Th>
                <Table.Th>{t('methodologies.publicationReason')}</Table.Th>
                <Table.Th>{t('methodologies.publicationChanges')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((entry) => (
                <Table.Tr key={String(entry.id)}>
                  <Table.Td>
                    <Timestamp value={entry.changedAt} />
                  </Table.Td>
                  <Table.Td>{entry.version}</Table.Td>
                  {/* ⚠ Ім'я, а не число; облікового запису вже немає — лише тоді ідентифікатор. */}
                  <Table.Td>{entry.changedByName ?? <Code>{entry.changedByUserId}</Code>}</Table.Td>
                  <Table.Td>{entry.changeReason ?? '—'}</Table.Td>
                  <Table.Td>{changedValues(entry.resultDiffJson) ?? '—'}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>
    </Stack>
  );
}
