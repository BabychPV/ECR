import { useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, List, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { entryReferenceKinds, entryReferences } from '@/features/registries/api';
import { purgeRegistryEntry, unretireRegistryEntry } from '@/features/registries/tree/treeApi';
import { invalidateRegistryTree } from '@/features/registries/tree/useRegistryTree';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import type { TreeNode } from '@/shared/ui/TreeEditor';
import { canRestoreWithAncestors, parentRetiredOf, type ParentRetired } from './nodeLifecycle';

/**
 * Підпис виду посилання з відмови `entryUsed` (`IRegistryStore.RegistryEntryHardReferences`). Ключі літеральні, щоб сторож
 * каталогу бачив кожен; невідомий вид (новіший сервер) - «Other», а не сирий код.
 */
export function referenceKindLabel(kind: string): string {
  switch (kind) {
    case 'cells': return t('registries.referenceKind.cells');
    case 'headerValues': return t('registries.referenceKind.headerValues');
    case 'registryValues': return t('registries.referenceKind.registryValues');
    case 'childEntries': return t('registries.referenceKind.childEntries');
    case 'links': return t('registries.referenceKind.links');
    case 'methodologyConstants': return t('registries.referenceKind.methodologyConstants');
    case 'methodologySubstances': return t('registries.referenceKind.methodologySubstances');
    case 'sourceEventValueMaps': return t('registries.referenceKind.sourceEventValueMaps');
    case 'snapshots': return t('registries.referenceKind.snapshots');
    case 'archivedCells': return t('registries.referenceKind.archivedCells');
    case 'matchRules': return t('registries.referenceKind.matchRules');
    default: return t('registries.referenceKind.other');
  }
}

export interface NodeLifecycleActionsProps {
  readonly registryCode: string;
  readonly node: TreeNode;
  /** Остаточно видалено: вузла більше немає, вибір у дереві знімається. */
  readonly onPurged: () => void;
}

/**
 * Закритий вузол структури NAE (людина 04.11): «Restore» повертає його в обіг, «Delete for good» видаляє фізично, якщо він
 * ніде не вживається. Видно лише на закритому вузлі (перемикач «Retired» у дереві), право - те саме, що й закрити.
 *
 * ⛔ Відмова не губиться в сповіщенні: закриті предки - біля кнопки з «Restore with parents»; «вживається» - у тому самому
 * діалозі підтвердження з розкладом за видами посилань (як `DeleteEntryDialog`), з недоступним підтвердженням.
 */
export function NodeLifecycleActions({ registryCode, node, onPurged }: NodeLifecycleActionsProps): JSX.Element {
  const queryClient = useQueryClient();
  const id = Number(node.id);
  const [refusal, setRefusal] = useState<{ readonly error: unknown; readonly parents: ParentRetired } | null>(null);
  const [confirming, setConfirming] = useState(false);

  const restore = useMutation({
    mutationFn: (withAncestors: boolean) => unretireRegistryEntry(registryCode, id, { withAncestors }),
    onSuccess: async (result) => {
      setRefusal(null);
      await invalidateRegistryTree(queryClient, registryCode);
      showDone(t('permits.nae.lifecycle.restored', { count: String(result.restoredIds.length) }));
    },
    onError: (error: unknown) => {
      const parents = parentRetiredOf(error);
      if (parents === null) {
        setRefusal(null);
        showApiError(error);

        return;
      }
      setRefusal({ error, parents });
    },
  });
  const restoring = usePendingLoading(restore.isPending);

  const purge = useMutation({
    mutationFn: () => purgeRegistryEntry(registryCode, id),
    onSuccess: async () => {
      setConfirming(false);
      await invalidateRegistryTree(queryClient, registryCode);
      showDone(t('permits.nae.lifecycle.deleted'));
      onPurged();
    },
    onError: (error: unknown) => {
      // «Вживається» лишається в діалозі; решта - сповіщенням.
      if (entryReferences(error) === null) showApiError(error);
    },
  });
  const purging = usePendingLoading(purge.isPending);
  const blocked = entryReferences(purge.error);
  const blockedBy = entryReferenceKinds(purge.error);

  return (
    <Stack gap="xs" data-nae-lifecycle="">
      <Text size="sm" c="dimmed">
        {t('permits.nae.lifecycle.retiredNote')}
      </Text>
      <Group gap="xs">
        <Button size="xs" loading={restoring} onClick={() => restore.mutate(false)} data-nae-restore="">
          {t('permits.nae.lifecycle.restore')}
        </Button>
        <Button
          size="xs"
          variant="outline"
          color="statusError"
          onClick={() => {
            purge.reset();
            setConfirming(true);
          }}
          data-nae-purge=""
        >
          {t('permits.nae.lifecycle.delete')}
        </Button>
      </Group>

      {refusal !== null && (
        <Alert color="statusWarning" role="alert" data-nae-restore-refused="">
          <Stack gap="xs">
            <Text size="sm">{problemText(refusal.error).detail ?? problemText(refusal.error).title}</Text>
            {canRestoreWithAncestors(refusal.parents) && (
              <Group>
                <Button size="xs" variant="light" loading={restoring} onClick={() => restore.mutate(true)} data-nae-restore-with-parents="">
                  {t('permits.nae.lifecycle.restoreWithParents', { count: String(refusal.parents.ancestorIds.length) })}
                </Button>
              </Group>
            )}
          </Stack>
        </Alert>
      )}

      <ConfirmModal
        opened={confirming}
        title={t('permits.nae.lifecycle.deleteTitle', { name: node.label })}
        text={blocked === null ? t('permits.nae.lifecycle.deleteText') : undefined}
        verb={t('permits.nae.lifecycle.delete')}
        danger
        confirmDisabled={blocked !== null}
        isPending={purging}
        onConfirm={() => purge.mutate()}
        onClose={() => setConfirming(false)}
      >
        {blocked !== null && (
          <Stack gap="sm" data-nae-purge-blocked="">
            <Group gap="xs" wrap="nowrap">
              <Badge color="statusWarning">{blocked}</Badge>
              <Text size="sm">{problemText(purge.error).detail ?? problemText(purge.error).title}</Text>
            </Group>
            {blockedBy.length > 0 && (
              <List size="sm" aria-label={t('registries.referencedBy')}>
                {blockedBy.map(({ kind, count }) => (
                  <List.Item key={kind}>
                    {referenceKindLabel(kind)}: {count}
                  </List.Item>
                ))}
              </List>
            )}
          </Stack>
        )}
      </ConfirmModal>
    </Stack>
  );
}
