import { useState, type JSX } from 'react';
import { Badge, Button, Group, Select, Skeleton, Stack, Table, Text, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { listDataSources } from '@/features/integration/dataSourceApi';
import { DataSourcesQueryKey } from '@/features/integration/dataSourcesKey';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  bindExternalKey,
  externalKeysKey,
  listExternalKeys,
  unbindExternalKey,
  type RegistryExternalKey,
} from './externalKeysApi';

/**
 * Зовнішні ідентифікатори запису довідника (`ФВ-8.10`, FEATURE-REGISTRY-SYNC S2):
 * за ними синк (`RegistrySyncJob`) знаходить запис для елемента джерела.
 *
 * ⛔ До S2 зв'язок не заводився нічим, крім SQL: синк читав зв'язки, яких ніхто
 * не міг створити, і кожен елемент джерела ставав подією «неприв'язаний».
 *
 * ⚠ Мінімальна панель: перелік, прив'язка, відв'язка. Події синку й пропозиції
 * зіставлення — S10.
 */
export function RegistryExternalKeysPanel({
  registryCode,
  entryId,
}: {
  registryCode: string;
  entryId: number;
}): JSX.Element {
  const queryClient = useQueryClient();
  const key = externalKeysKey(registryCode, entryId);

  const links = useQuery({ queryKey: key, queryFn: () => listExternalKeys(registryCode, entryId) });
  const sources = useQuery({ queryKey: DataSourcesQueryKey, queryFn: listDataSources });

  const [dataSourceId, setDataSourceId] = useState<string | null>(null);
  const [externalId, setExternalId] = useState('');
  const [removing, setRemoving] = useState<RegistryExternalKey | null>(null);

  const bind = useMutation({
    mutationFn: () =>
      bindExternalKey(registryCode, {
        entryId,
        dataSourceId: Number(dataSourceId),
        externalId: externalId.trim(),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: key });
      setExternalId('');
      showDone(t('registries.externalKeyAdded'));
    },
    onError: showApiError,
  });

  const unbind = useMutation({
    mutationFn: (id: number) => unbindExternalKey(registryCode, id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: key });
      setRemoving(null);
      showDone(t('registries.externalKeyRemoved'));
    },
    onError: showApiError,
  });

  const items = links.data?.items ?? [];

  return (
    <Stack gap="xs" mt="md" data-registry-external-keys>
      <Title order={5}>{t('registries.externalKeys')}</Title>
      <Text size="sm" c="dimmed">
        {t('registries.externalKeysHint')}
      </Text>

      {links.error !== null ? (
        <ErrorAlert error={links.error} onRetry={() => void links.refetch()} />
      ) : links.isPending ? (
        <Skeleton height={48} radius="sm" />
      ) : items.length === 0 ? (
        <Text size="sm">{t('registries.externalKeysEmpty')}</Text>
      ) : (
        <Table aria-label={t('registries.externalKeys')}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('registries.externalKeySource')}</Table.Th>
              <Table.Th>{t('registries.externalKeyId')}</Table.Th>
              <Table.Th>{t('registries.externalKeyPath')}</Table.Th>
              <Table.Th>{t('registries.externalKeyMissing')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {items.map((link) => (
              <Table.Tr key={link.id}>
                <Table.Td>{link.dataSourceCode}</Table.Td>
                <Table.Td>{link.externalId}</Table.Td>
                <Table.Td>{link.externalPath ?? ''}</Table.Td>
                <Table.Td>
                  {/* D-212: синк не знайшов елемент у повному знімку джерела — відтоді й досі. */}
                  {link.missingInSourceSince != null && (
                    <Badge color="statusWarning" variant="light" data-external-key-missing={link.id}>
                      {t('registries.externalKeyMissingSince', {
                        date: formatDateTime(link.missingInSourceSince),
                      })}
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  <Button size="xs" variant="subtle" color="statusError" onClick={() => setRemoving(link)}>
                    {t('registries.externalKeyRemove')}
                  </Button>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      {sources.error !== null ? (
        <ErrorAlert error={sources.error} onRetry={() => void sources.refetch()} />
      ) : (
        <Group align="flex-end" gap="xs">
          <Select
            label={t('registries.externalKeySource')}
            data={(sources.data ?? []).map((source) => ({ value: String(source.id), label: source.code }))}
            value={dataSourceId}
            onChange={setDataSourceId}
          />
          <TextInput
            label={t('registries.externalKeyId')}
            value={externalId}
            onChange={(event) => setExternalId(event.currentTarget.value)}
          />
          <Button
            disabled={dataSourceId === null || externalId.trim().length === 0}
            loading={bind.isPending}
            onClick={() => bind.mutate()}
          >
            {t('registries.externalKeyAdd')}
          </Button>
        </Group>
      )}

      <ConfirmModal
        opened={removing !== null}
        title={t('registries.externalKeyRemoveTitle', { externalId: removing?.externalId ?? '' })}
        text={t('registries.externalKeyRemoveText')}
        verb={t('registries.externalKeyRemove')}
        isPending={unbind.isPending}
        onConfirm={() => {
          if (removing !== null) unbind.mutate(removing.id);
        }}
        onClose={() => setRemoving(null)}
      />
    </Stack>
  );
}
