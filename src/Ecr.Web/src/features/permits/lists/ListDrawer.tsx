import { useState, type JSX } from 'react';
import { Button, Group, List, Stack, Text, TextInput } from '@mantine/core';
import { Link } from 'react-router-dom';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { language, t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { KeyValue } from '@/shared/ui/KeyValue';
import { showApiError, showDone } from '@/shared/ui/notify';
import { textOf } from '../typeEditor/labels';
import { typeEditorPath } from '../shell/permitPaths';
import { keptIn } from './ListsColumns';
import type { PermitListChange, PermitListDetail, PermitListValue } from './listsApi';
import { kindLabel, usedCountText, valueLabels, type ListRow, type ListUse } from './listsModel';
import { useChangeListItems, useListDetail } from './useLists';
import './lists.css';

/** Скільки значень показує шторка лише для читання; решта — «ще N» і довідник. */
export const DrawerValueLimit = 12;

/** Скільки значень показує шторка, де їх можна правити: без ліміту додане наприкінці значення не було б видно. */
export const DrawerEditLimit = 200;

function usedText(use: ListUse): string {
  return t('permits.lists.drawer.usedColumn', { type: use.typeName, table: use.tableName, column: use.columnName });
}

function UsedItem({ use }: { readonly use: ListUse }): JSX.Element {
  return <List.Item>{usedText(use)}</List.Item>;
}

interface ValuesProps {
  readonly row: ListRow;
  readonly detail: PermitListDetail;
}

/** Значення списку: читання, а з правом — додати, перейменувати, прибрати (кожна дія — окремий запит `PUT …/items`). */
function ValueList({ row, detail }: ValuesProps): JSX.Element {
  const change = useChangeListItems(row.id);
  const saving = usePendingLoading(change.isPending);
  const [editing, setEditing] = useState<number | null>(null);
  const [text, setText] = useState('');
  const [fresh, setFresh] = useState('');
  const limit = row.editable ? DrawerEditLimit : DrawerValueLimit;
  const shown = detail.items.slice(0, limit);

  function apply(changes: readonly PermitListChange[], done: string, after: () => void): void {
    if (change.isPending) return;
    change.mutate(changes, {
      onSuccess: () => {
        showDone(done);
        after();
      },
      onError: showApiError,
    });
  }

  function add(): void {
    if (fresh.trim() === '') return;
    apply(
      [{ action: 'Add', id: null, displayL10n: valueLabels(fresh, undefined, language()) }],
      t('permits.lists.drawer.added'),
      () => setFresh(''),
    );
  }

  function rename(item: PermitListValue): void {
    if (text.trim() === '') return;
    apply(
      [{ action: 'Update', id: item.id, displayL10n: valueLabels(text, item.displayL10n, language()) }],
      t('permits.lists.drawer.renamed'),
      () => setEditing(null),
    );
  }

  function remove(item: PermitListValue): void {
    apply([{ action: 'Remove', id: item.id, displayL10n: null }], t('permits.lists.drawer.removed'), () => setEditing(null));
  }

  return (
    <Stack gap="xs" data-list-values="">
      {shown.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('permits.lists.drawer.noValues')}
        </Text>
      ) : (
        <Stack component="ul" gap="xs" aria-label={t('permits.lists.drawer.values')} className="pl-values">
          {shown.map((item) => {
            const label = textOf(item.displayL10n) || item.code;

            return editing === item.id ? (
              <li key={item.id}>
                <form
                  onSubmit={(event) => {
                    event.preventDefault();
                    rename(item);
                  }}
                >
                  <Group align="flex-end" gap="xs" wrap="nowrap">
                    <TextInput
                      label={t('permits.lists.drawer.edit.label')}
                      value={text}
                      maxLength={256}
                      onChange={(event) => setText(event.currentTarget.value)}
                      style={{ flex: 1 }}
                      data-list-edit=""
                    />
                    <Button type="submit" loading={saving} disabled={text.trim() === ''}>
                      {t('permits.lists.drawer.edit.save')}
                    </Button>
                    <Button variant="default" onClick={() => setEditing(null)}>
                      {t('permits.lists.drawer.edit.cancel')}
                    </Button>
                  </Group>
                </form>
              </li>
            ) : (
              <li key={item.id}>
                <Group justify="space-between" gap="xs" wrap="nowrap">
                  <Text size="sm">{label}</Text>
                  {row.editable && (
                    <Group gap="xs" wrap="nowrap">
                      <Button
                        variant="subtle"
                        size="xs"
                        aria-label={t('permits.lists.drawer.renameAria', { value: label })}
                        onClick={() => {
                          setEditing(item.id);
                          setText(label);
                        }}
                      >
                        {t('permits.lists.drawer.rename')}
                      </Button>
                      <Button
                        variant="subtle"
                        color="statusError"
                        size="xs"
                        aria-label={t('permits.lists.drawer.removeAria', { value: label })}
                        disabled={change.isPending}
                        onClick={() => remove(item)}
                      >
                        {t('permits.lists.drawer.remove')}
                      </Button>
                    </Group>
                  )}
                </Group>
              </li>
            );
          })}
        </Stack>
      )}
      {detail.items.length > shown.length && (
        <Text size="sm" c="dimmed">
          {t('permits.lists.drawer.more', { count: detail.items.length - shown.length })}
        </Text>
      )}
      {row.editable && (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            add();
          }}
        >
          <Group align="flex-end" gap="xs" wrap="nowrap">
            <TextInput
              label={t('permits.lists.drawer.add.label')}
              value={fresh}
              maxLength={256}
              onChange={(event) => setFresh(event.currentTarget.value)}
              style={{ flex: 1 }}
              data-list-add=""
            />
            <Button type="submit" variant="default" loading={saving} disabled={fresh.trim() === ''}>
              {t('permits.lists.drawer.add.submit')}
            </Button>
          </Group>
        </form>
      )}
    </Stack>
  );
}

function Values({ row }: { readonly row: ListRow }): JSX.Element {
  const detail = useListDetail(row.id);

  return (
    <AsyncBoundary<PermitListDetail>
      isPending={detail.isPending}
      error={detail.error}
      data={detail.data}
      onRetry={() => void detail.refetch()}
    >
      {(loaded) => <ValueList row={row} detail={loaded} />}
    </AsyncBoundary>
  );
}

function note(row: ListRow): string {
  if (row.kind === 'shared') return t('permits.lists.drawer.note.shared');
  if (row.kind === 'type') {
    return row.editable
      ? t('permits.lists.drawer.note.typeEditable', { owner: row.owner })
      : t('permits.lists.drawer.note.type', { owner: row.owner });
  }

  return t('permits.lists.drawer.note.permit');
}

/**
 * Шторка списку (макет `screens-permits.js`, екран Lists). Значення читаються з `GET /permit-lists/{key}`; з правом
 * (`row.editable` — рішення сервера) їх додають, перейменовують і прибирають тут же (PM-08e). Список-таблиця дозволу
 * значень не має: це рядки самого дозволу.
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
            { label: t('permits.lists.col.used'), value: usedCountText(row.used.length) },
          ]}
        />
        <Text size="sm">{note(row)}</Text>
        {row.kind !== 'permit' && <Values row={row} />}
        <Stack gap="xs">
          <Text fw={600} size="sm">
            {t('permits.lists.drawer.usedBy')}
          </Text>
          <List size="sm" data-list-used="">
            {row.used.map((use) => (
              <UsedItem key={`${use.typeCode}:${use.tableCode}:${use.columnCode}`} use={use} />
            ))}
          </List>
        </Stack>
      </Stack>
    </DetailDrawer>
  );
}
