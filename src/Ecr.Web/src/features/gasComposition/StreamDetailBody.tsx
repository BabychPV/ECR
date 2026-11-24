import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, Stack, Table, Text, TextInput, Title, Tooltip, VisuallyHidden } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { KeyValue } from '@/shared/ui/KeyValue';
import { showApiError } from '@/shared/ui/notify';
import {
  deleteVersion,
  deviates,
  lastMonthOf,
  monthOf,
  renameCase,
  type GasCase,
  type GasChangeResult,
  type GasComponent,
  type GasStreamDetail,
  type GasVersion,
} from './api';
import { VersionEditorModal, type VersionEditorTarget } from './VersionEditorModal';

interface Props {
  readonly stream: GasStreamDetail;
  readonly components: readonly GasComponent[];
  readonly onChanged: (result: GasChangeResult) => void;
}

const yesNo = (value: boolean): string => (value ? t('gasComposition.yes') : t('gasComposition.no'));

/** «По який місяць» для тексту діалогу; без кінця — «без окончания». */
const untilText = (validTo: string | null | undefined): string =>
  validTo === null || validTo === undefined ? t('gasComposition.openEnded') : lastMonthOf(validTo);

/** Σ версії текстом: до шести знаків, без хвостових нулів. */
const sumText = (sum: string): string => String(Math.round(Number(sum) * 1e6) / 1e6);

/**
 * Шторка потоку (D-NEW): атрибути і кейси, кожен — шкалою версій «з → по» (чинна, замкнена, Σ). Дії: нова версія
 * (копія попередньої), відкрити/змінити, видалити (попередня продовжується), новий кейс, перейменувати кейс.
 */
export default function StreamDetailBody({ stream, components, onChanged }: Props): JSX.Element {
  const [editor, setEditor] = useState<VersionEditorTarget | null>(null);
  const [deleting, setDeleting] = useState<{ version: GasVersion; previous: GasVersion | undefined } | null>(null);
  const [renaming, setRenaming] = useState<GasCase | null>(null);
  const [newName, setNewName] = useState('');

  const remove = useMutation({
    mutationFn: (versionId: number) => deleteVersion(versionId),
    onSuccess: (result) => {
      setDeleting(null);
      onChanged(result);
    },
    onError: showApiError,
  });

  const rename = useMutation({
    mutationFn: () => renameCase(stream.id, renaming?.name ?? '', newName.trim()),
    onSuccess: (result) => {
      setRenaming(null);
      onChanged(result);
    },
    onError: showApiError,
  });

  return (
    <Stack gap="md" data-testid="gas-stream-detail">
      <KeyValue
        items={[
          { label: t('gasComposition.detail.streamId'), value: String(stream.streamId), mono: true },
          { label: t('gasComposition.col.number'), value: stream.number ?? '—' },
          {
            label: t('gasComposition.location'),
            value: stream.isOnshore ? t('gasComposition.onshore') : t('gasComposition.offshore'),
          },
          { label: t('gasComposition.sour'), value: yesNo(stream.isSour) },
          { label: t('gasComposition.detail.hmb'), value: yesNo(stream.isHmb) },
          { label: t('gasComposition.detail.hmbDoc'), value: stream.hmbDoc ?? '—' },
          { label: t('gasComposition.detail.group'), value: stream.group === null || stream.group === undefined ? '—' : String(stream.group) },
          { label: t('gasComposition.detail.hidden'), value: yesNo(stream.hidden) },
        ]}
      />

      <Group justify="space-between" align="center">
        <Title order={3} size="h5">
          {t('gasComposition.cases')}
        </Title>
        {stream.canEdit && (
          <Button size="xs" variant="default" onClick={() => setEditor({ kind: 'newCase' })} data-testid="gas-new-case">
            {t('gasComposition.newCase')}
          </Button>
        )}
      </Group>

      {stream.cases.length === 0 && (
        <Text size="sm" c="dimmed">
          {t('gasComposition.noCases')}
        </Text>
      )}

      {stream.cases.map((item) => {
        const last = item.versions[item.versions.length - 1];
        const caseLocked = item.versions.some((v) => v.locks.length > 0);
        return (
          <Stack key={item.name} gap="xs" data-testid="gas-case" data-case={item.name}>
            <Group justify="space-between" align="center" wrap="wrap" gap="xs">
              <Text fw={600}>{item.name}</Text>
              {stream.canEdit && (
                <Group gap="xs">
                  {!caseLocked && (
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      onClick={() => {
                        setNewName(item.name);
                        setRenaming(item);
                      }}
                    >
                      {t('gasComposition.renameCase')}
                    </Button>
                  )}
                  <Button
                    size="compact-xs"
                    variant="light"
                    data-testid="gas-add-version"
                    onClick={() => setEditor({ kind: 'new', caseName: item.name, copyFrom: last })}
                  >
                    {t('gasComposition.addVersion')}
                  </Button>
                </Group>
              )}
            </Group>
            <Table withTableBorder verticalSpacing={4} aria-label={t('gasComposition.caseName') + ': ' + item.name}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">{t('gasComposition.from')}</Table.Th>
                  <Table.Th scope="col">{t('gasComposition.to')}</Table.Th>
                  <Table.Th scope="col" ta="end">
                    {t('gasComposition.sum')}
                  </Table.Th>
                  <Table.Th scope="col">
                    <VisuallyHidden>{t('common.actions')}</VisuallyHidden>
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {[...item.versions].reverse().map((version) => {
                  const index = item.versions.indexOf(version);
                  const previous = index > 0 ? item.versions[index - 1] : undefined;
                  const locked = version.locks.length > 0;
                  const readOnly = !stream.canEdit || locked;
                  return (
                    <Table.Tr key={version.id} data-testid="gas-version-row" data-from={version.validFrom}>
                      <Table.Td>
                        <Text size="sm" span>
                          {monthOf(version.validFrom)}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        {version.validTo === null || version.validTo === undefined ? (
                          <Badge size="sm" variant="light" color="statusSuccess">
                            {t('gasComposition.current')}
                          </Badge>
                        ) : (
                          <Text size="sm" span>
                            {lastMonthOf(version.validTo)}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td ta="end">
                        {version.noComposition ? (
                          <Badge size="sm" variant="light" color="statusWarning">
                            {t('gasComposition.noComposition')}
                          </Badge>
                        ) : (
                          <Text size="sm" span {...(deviates(Number(version.sum)) ? { c: 'statusWarning', fw: 600 } : {})}>
                            {sumText(version.sum)}
                          </Text>
                        )}
                        {locked && (
                          <Tooltip label={t('gasComposition.lockedHint')} multiline w={260}>
                            <Badge size="sm" variant="outline" color="gray" ml="xs" data-testid="gas-version-lock">
                              {t('gasComposition.locked', { periods: version.locks.map((l) => l.period).join(', ') })}
                            </Badge>
                          </Tooltip>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Group gap="xs" justify="flex-end" wrap="nowrap">
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            onClick={() =>
                              setEditor({ kind: 'edit', caseName: item.name, version, previous, readOnly })
                            }
                          >
                            {readOnly ? t('gasComposition.view') : t('gasComposition.edit')}
                          </Button>
                          {!readOnly && (
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              color="statusError"
                              onClick={() => {
                                remove.reset();
                                setDeleting({ version, previous });
                              }}
                            >
                              {t('gasComposition.deleteVersion')}
                            </Button>
                          )}
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Stack>
        );
      })}

      <VersionEditorModal
        streamId={stream.id}
        target={editor}
        components={components}
        onClose={() => setEditor(null)}
        onSaved={(result) => {
          setEditor(null);
          onChanged(result);
        }}
      />

      <ConfirmModal
        opened={deleting !== null}
        title={t('gasComposition.deleteVersionTitle', { from: deleting === null ? '' : monthOf(deleting.version.validFrom) })}
        verb={t('common.delete')}
        isPending={remove.isPending}
        onConfirm={() => {
          if (deleting !== null) remove.mutate(deleting.version.id);
        }}
        onClose={() => setDeleting(null)}
      >
        <Text size="sm">
          {deleting?.previous !== undefined && deleting.previous.validTo === deleting.version.validFrom
            ? t('gasComposition.deleteVersionExtends', { to: untilText(deleting.version.validTo) })
            : t('gasComposition.deleteVersionGap', { to: untilText(deleting?.version.validTo) })}
        </Text>
      </ConfirmModal>

      <Modal
        opened={renaming !== null}
        onClose={() => setRenaming(null)}
        title={t('gasComposition.renameCaseTitle', { case: renaming?.name ?? '' })}
      >
        <Stack gap="sm">
          <TextInput
            label={t('gasComposition.caseName')}
            value={newName}
            maxLength={100}
            data-autofocus
            onChange={(event) => setNewName(event.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRenaming(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              disabled={newName.trim().length === 0 || rename.isPending}
              loading={rename.isPending}
              onClick={() => {
                if (renaming === null || rename.isPending) return;
                rename.mutate();
              }}
            >
              {t('gasComposition.editor.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
