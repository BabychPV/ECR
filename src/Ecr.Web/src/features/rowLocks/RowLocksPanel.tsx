import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, Stack, Table, Text, VisuallyHidden } from '@mantine/core';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { t } from '@/shared/i18n';
import { lockDate, lockOwner } from './rowLockModel';
import { useReleaseRowLock } from './useRowLocks';

export interface RowLocksPanelProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Блокування цієї таблиці (чинні й ще не початі). */
  readonly locks: readonly RowLockDto[];
  /** Назва рядка людині за ключем (підпис або «рядок N»). */
  readonly rowName: (rowKey: string) => string;
  readonly onClose: () => void;
}

/**
 * ✎ B139 (B75): перелік блокувань рядків таблиці — хто, з якого й до якого дня, примітка — і «Зняти» там, де
 * сервер дозволяє (`canUnlock`: власник блокування або носій права `Document.RowLock`). Кнопки «Зняти» без
 * дозволу немає: вона гарантовано дала б `403`.
 *
 * ⚠ Зняття не видаляє історії (сервер ставить `ReleasedAt`), тож підтвердження не питаємо — блокування можна
 * поставити знову тим самим діалогом.
 *
 * ⛔ Лінивий модуль (`D-132`).
 */
export default function RowLocksPanel({ documentId, periodKey, locks, rowName, onClose }: RowLocksPanelProps): JSX.Element {
  const [releasing, setReleasing] = useState<number | null>(null);
  const release = useReleaseRowLock(documentId, periodKey, () => setReleasing(null));

  return (
    <Modal opened onClose={onClose} title={t('rowLock.listTitle')} size="lg" data-row-locks-panel>
      {locks.length === 0 ? (
        <Text size="sm" c="dimmed" data-row-locks-empty>
          {t('rowLock.listEmpty')}
        </Text>
      ) : (
        <Stack gap="sm">
          <Table striped withTableBorder aria-label={t('rowLock.listTitle')}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col">{t('rowLock.colRow')}</Table.Th>
                <Table.Th scope="col">{t('rowLock.colPeriod')}</Table.Th>
                <Table.Th scope="col">{t('rowLock.colOwner')}</Table.Th>
                <Table.Th scope="col">{t('rowLock.colNote')}</Table.Th>
                <Table.Th scope="col">
                  <VisuallyHidden>{t('rowLock.colActions')}</VisuallyHidden>
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {locks.map((lock) => (
                <Table.Tr key={lock.id} data-row-lock-item={lock.id}>
                  <Table.Td>{rowName(lock.rowKey)}</Table.Td>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Text size="sm">{t('rowLock.range', { from: lockDate(lock.lockFrom), to: lockDate(lock.lockUntil) })}</Text>
                      {!lock.isActive && (
                        <Badge variant="light" color="gray" size="sm">
                          {t('rowLock.notStarted')}
                        </Badge>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>{lockOwner(lock)}</Table.Td>
                  <Table.Td>{lock.note ?? ''}</Table.Td>
                  <Table.Td>
                    {lock.canUnlock && (
                      <Button
                        size="xs"
                        variant="default"
                        loading={release.isPending && releasing === lock.id}
                        disabled={release.isPending && releasing !== lock.id}
                        aria-label={t('rowLock.releaseRow', { row: rowName(lock.rowKey) })}
                        onClick={() => {
                          if (release.isPending) return;
                          setReleasing(lock.id);
                          release.mutate(lock.id, { onError: () => setReleasing(null) });
                        }}
                        data-row-lock-release={lock.id}
                      >
                        {t('rowLock.release')}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Stack>
      )}
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('rowLock.close')}
        </Button>
      </Group>
    </Modal>
  );
}
