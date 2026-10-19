import type { JSX } from 'react';
import { Button, Group, List, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { KeyValue } from '@/shared/ui/KeyValue';
import { typeEditorPath } from '../shell/permitPaths';
import { keptIn } from './ListsColumns';
import { kindLabel, type ListRow, type ListUse } from './listsModel';
import { useListEntries } from './useLists';
import type { RegistryEntryDto } from '@/api/types';

/** Скільки значень показує шторка; решта — «ще N» і довідник. */
export const DrawerValueLimit = 12;

function usedText(use: ListUse): string {
  return use.tableCode === null
    ? t('permits.lists.drawer.usedHeader', { type: use.typeName, column: use.columnName })
    : t('permits.lists.drawer.usedColumn', { type: use.typeName, table: use.tableName, column: use.columnName });
}

function UsedItem({ use }: { readonly use: ListUse }): JSX.Element {
  return <List.Item>{usedText(use)}</List.Item>;
}

function Values({ values }: { readonly values: readonly string[] }): JSX.Element {
  if (values.length === 0) return <Text size="sm" c="dimmed">{t('permits.lists.drawer.noValues')}</Text>;
  const shown = values.slice(0, DrawerValueLimit);

  return (
    <Stack gap="xs" data-list-values="">
      <List listStyleType="none" spacing="xs" aria-label={t('permits.lists.drawer.values')}>
        {shown.map((value, index) => (
          <List.Item key={`${value}:${index}`}>{value}</List.Item>
        ))}
      </List>
      {values.length > shown.length && (
        <Text size="sm" c="dimmed">
          {t('permits.lists.drawer.more', { count: values.length - shown.length })}
        </Text>
      )}
    </Stack>
  );
}

function SharedValues({ code }: { readonly code: string }): JSX.Element {
  const entries = useListEntries(code);

  return (
    <AsyncBoundary<RegistryEntryDto[]>
      isPending={entries.isPending}
      error={entries.error}
      data={entries.data}
      onRetry={() => void entries.refetch()}
    >
      {(loaded) => <Values values={loaded.map((entry) => entry.display)} />}
    </AsyncBoundary>
  );
}

/**
 * Шторка списку (макет `screens-permits.js`, екран Lists). Варіант A (PM-08d): шторка лише ЧИТАЄ.
 * Спільні значення додаються в «Реєстрах», значення «лише тип» — у конструкторі типу; обидва шляхи
 * названо в тексті й кнопках. Окремого ендпоінта списків немає.
 */
export function ListDrawer({ row }: { readonly row: ListRow }): JSX.Element {
  return (
    <DetailDrawer
      panelId={row.id}
      title={row.name}
      subtitle={row.kind === 'shared' ? kindLabel(row.kind) : `${kindLabel(row.kind)} · ${row.owner}`}
      closeLabel={t('permits.lists.drawer.close')}
      footer={
        <Group justify="flex-end" gap="xs">
          {row.kind === 'shared' && row.registryCode !== null ? (
            <Button
              component={Link}
              to={`/admin/registries/${encodeURIComponent(row.registryCode)}/entries`}
              data-list-open="registry"
            >
              {t('permits.lists.action.registries')}
            </Button>
          ) : (
            <Button component={Link} to={typeEditorPath(row.typeCode)} data-list-open="type">
              {t('permits.lists.action.openType')}
            </Button>
          )}
        </Group>
      }
    >
      <Stack gap="md" data-list-drawer={row.kind}>
        <KeyValue
          items={[
            { label: t('permits.lists.drawer.kind'), value: kindLabel(row.kind) },
            { label: t('permits.lists.drawer.kept'), value: keptIn(row) },
          ]}
        />
        <Text size="sm">
          {row.kind === 'shared'
            ? t('permits.lists.drawer.note.shared')
            : row.kind === 'type'
              ? t('permits.lists.drawer.note.type', { owner: row.owner })
              : t('permits.lists.drawer.note.permit')}
        </Text>
        {row.kind === 'shared' && row.registryCode !== null && <SharedValues code={row.registryCode} />}
        {row.kind === 'type' && <Values values={row.choices} />}
        <Stack gap="xs">
          <Text fw={600} size="sm">
            {t('permits.lists.drawer.usedBy')}
          </Text>
          <List size="sm" data-list-used="">
            {row.used.map((use) => (
              <UsedItem key={`${use.typeCode}:${use.tableCode ?? '_'}:${use.columnCode}`} use={use} />
            ))}
          </List>
        </Stack>
      </Stack>
    </DetailDrawer>
  );
}
