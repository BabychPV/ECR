import { useMemo, useState, type JSX } from 'react';
import { Button, Group, Loader, Select, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, SourceEntityStatus } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { RegistrySyncPolicy } from '@/features/sources/registrySyncPolicyApi';
import { canEditRegistrySyncPolicy, RegistrySyncPolicyModal } from '@/features/sources/RegistrySyncPolicyModal';
import { AddSourceEntityModal } from './AddSourceEntityModal';
import type { DataSource } from './dataSourceApi';
import { bindSourceEntityRegistry, SourceEntitiesQueryKey } from './sourceEntityApi';

/** Значення «не прив'язана» у виборі довідника: `Select` не приймає `null` як опцію. */
const Unbound = '';

/**
 * Чинна політика синку сутності (`D-212`) — з рядка `GET /api/v1/sources`:
 * `SourceEntityStatus` несе всі чотири поля обов'язковими, тож форма завжди
 * стартує з того, що лежить у базі, а не з типової.
 */
function policyOf(row: SourceEntityStatus): RegistrySyncPolicy {
  return {
    onMissingInSource: row.onMissingInSource,
    validFromAttribute: row.validFromAttribute,
    validToAttribute: row.validToAttribute,
    validToInclusive: row.validToInclusive,
  };
}

/**
 * Вкладка «Entities» шухляди з'єднання (`ФВ-13.11`, `ФВ-8.11`).
 *
 * ⛔ До неї сутність збору з вебу не заводилась узагалі — лише сідом чи SQL.
 * Тут: перелік сутностей цього з'єднання, кнопка «Додати» з каталогу джерела
 * і прив'язка кожної до довідника, без якої мапінг на поле довідника сервер
 * відхиляє.
 *
 * ⚠ Сутності — з того самого запиту `['sources']`, що й таблиця та вкладка
 * розкладу; довідники — з `queryKeys.registries.list()`, як у редакторі колонки.
 */
export function SourceEntitiesTab({ source }: { readonly source: DataSource }): JSX.Element {
  const queryClient = useQueryClient();
  const [adding, setAdding] = useState(false);
  const session = useSession();
  const [policyFor, setPolicyFor] = useState<number | null>(null);

  const entities = useQuery({
    queryKey: SourceEntitiesQueryKey,
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
  });

  const bind = useMutation({
    mutationFn: ({ id, registryDefId }: { id: number; registryDefId: number | null }) =>
      bindSourceEntityRegistry(id, registryDefId),
    onSuccess: (entity) => {
      void queryClient.invalidateQueries({ queryKey: SourceEntitiesQueryKey });
      notifications.show({
        message: entity.registryDefId === null ? t('sources.registryUnbound') : t('sources.registryBound'),
      });
    },
  });

  const options = useMemo(
    () => [
      { value: Unbound, label: t('sources.registryNone') },
      ...(registries.data ?? []).map((registry) => {
        const name = localized(registry.nameL10n);

        return { value: String(registry.id), label: name.length > 0 ? name : registry.code };
      }),
    ],
    [registries.data],
  );

  if (entities.isError) {
    return <ErrorAlert error={entities.error} onRetry={() => void entities.refetch()} />;
  }

  if (entities.isPending) return <Loader size="sm" />;

  const own = entities.data.filter((entity) => entity.dataSourceId === source.id);
  const editing = own.find((entity) => entity.id === policyFor);

  return (
    <Stack gap="sm" data-entities-tab={source.code}>
      <Group justify="space-between">
        <Text size="xs" c="dimmed">
          {t('sources.registryHint')}
        </Text>
        <Button size="xs" onClick={() => setAdding(true)} data-add-entity-open="">
          {t('sources.addEntity')}
        </Button>
      </Group>

      {/* ⛔ L10: відмова читання довідників — видно, а вибір вимкнено. */}
      {registries.isError && (
        <ErrorAlert error={registries.error} onRetry={() => void registries.refetch()} />
      )}
      {bind.error !== null && <ErrorAlert error={bind.error} />}

      {own.length === 0 ? (
        <Text size="sm" c="dimmed" data-entities-empty="">
          {t('sources.scheduleNoEntities')}
        </Text>
      ) : (
        <Table data-entities="">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('sources.entity')}</Table.Th>
              <Table.Th>{t('sources.registry')}</Table.Th>
              <Table.Th>{t('sources.syncPolicy')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {own.map((entity) => {
              const label = entity.displayName ?? entity.code;

              return (
                <Table.Tr key={entity.id} data-entity-row={entity.code}>
                  <Table.Td>
                    <Text size="sm">{label}</Text>
                    {entity.entityPath !== null && (
                      <Text size="xs" c="dimmed" ff="monospace">
                        {entity.entityPath}
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Select
                      aria-label={t('sources.registry')}
                      data={options}
                      value={entity.registryDefId == null ? Unbound : String(entity.registryDefId)}
                      disabled={!registries.isSuccess || bind.isPending}
                      allowDeselect={false}
                      onChange={(value) =>
                        bind.mutate({
                          id: entity.id,
                          registryDefId: value === null || value === Unbound ? null : Number(value),
                        })
                      }
                      data-entity-registry={entity.code}
                    />
                  </Table.Td>
                  <Table.Td>
                    {/* Політика — лише для прив'язаної сутності й лише з правом на дані довідника. */}
                    {entity.registryDefId != null &&
                      canEditRegistrySyncPolicy(session.data, entity.registryDefId) && (
                        <Button
                          size="xs"
                          variant="default"
                          onClick={() => setPolicyFor(entity.id)}
                          data-entity-sync-policy={entity.code}
                        >
                          {t('sources.syncPolicy')}
                        </Button>
                      )}
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
      )}

      <AddSourceEntityModal
        dataSourceId={source.id}
        sourceName={source.code}
        opened={adding}
        onClose={() => setAdding(false)}
      />

      {editing !== undefined && (
        <RegistrySyncPolicyModal
          key={editing.id}
          entityId={editing.id}
          entityLabel={editing.displayName ?? editing.code}
          current={policyOf(editing)}
          opened
          onClose={() => setPolicyFor(null)}
          onSaved={() => void queryClient.invalidateQueries({ queryKey: SourceEntitiesQueryKey })}
        />
      )}
    </Stack>
  );
}
