import { useState, type JSX } from 'react';
import { Badge, Code, Group, Modal, Select, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { methodologyVersionDiff } from '@/features/methodologies/api';
import {
  defaultBaseVersion,
  DiffKinds,
  groupDiff,
  type DiffKind,
  type MethodologyDiffItem,
} from '@/features/methodologies/versionDiff';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { toneFills, type StatusTone } from '@/shared/ui/StatusBadge';

export interface VersionDiffModalProps {
  readonly methodologyId: number;
  readonly versions: readonly MethodologyDraftVersionDto[];

  /** Версія «стало»; `null` — діалог закритий. */
  readonly target: MethodologyDraftVersionDto | null;
  readonly onClose: () => void;
}

/**
 * Порівняння двох версій методології (`BE-25`, макет `mv-compare`).
 *
 * ⛔ Лише формули, константи й тест-кейси — рівно те, що порівнює сервер.
 * Правил прив'язки й числового режиму у відповіді немає, тож їх немає й тут:
 * порожня група «правила» читалася б як «правила однакові», а це неправда.
 */
export function VersionDiffModal({
  methodologyId,
  versions,
  target,
  onClose,
}: VersionDiffModalProps): JSX.Element {
  return (
    <Modal
      opened={target !== null}
      onClose={onClose}
      title={t('methodologies.compareTitle', { version: target?.versionNumber ?? '' })}
      size="xl"
    >
      {/* ⚠ `key`: база за замовчуванням рахується наново для кожної версії. */}
      {target !== null && (
        <DiffBody key={target.id} methodologyId={methodologyId} versions={versions} target={target} />
      )}
    </Modal>
  );
}

function DiffBody({
  methodologyId,
  versions,
  target,
}: {
  methodologyId: number;
  versions: readonly MethodologyDraftVersionDto[];
  target: MethodologyDraftVersionDto;
}): JSX.Element {
  const [baseId, setBaseId] = useState<string | null>(() => {
    const base = defaultBaseVersion(versions, target.id);
    return base === undefined ? null : String(base.id);
  });

  const diff = useQuery({
    // ⚠ Префікс `methodologies` — щоб інвалідизація домену зачіпала й це.
    queryKey: ['methodologies', 'versionDiff', methodologyId, target.id, baseId],
    queryFn: () => methodologyVersionDiff(methodologyId, target.id, Number(baseId)),
    enabled: baseId !== null,
  });

  return (
    <Stack gap="sm">
      <Select
        label={t('methodologies.compareBase')}
        description={t('methodologies.compareBaseHint')}
        allowDeselect={false}
        value={baseId}
        data={versions
          .filter((version) => version.id !== target.id)
          .map((version) => ({
            value: String(version.id),
            label: `${version.versionNumber} · ${versionStateLabel(version.status)}`,
          }))}
        onChange={setBaseId}
      />

      <Text size="sm" c="dimmed">
        {t('methodologies.compareScope')}
      </Text>

      {/* ⛔ Відмова — окремо, не порожнім результатом (L10): «змін немає» на
          відмові сервера означало б «можна публікувати, нічого не змінилося». */}
      {baseId !== null && (
        <AsyncBoundary<MethodologyDiffItem[]>
          isPending={diff.isPending}
          error={diff.error}
          data={diff.data?.items}
          isEmpty={() => false}
          skeleton="table"
          onRetry={() => void diff.refetch()}
        >
          {(items) => <DiffResult items={items} />}
        </AsyncBoundary>
      )}
    </Stack>
  );
}

/** Результат порівняння; порожній перелік — окреме речення, без жодного бейджа. */
export function DiffResult({ items }: { items: readonly MethodologyDiffItem[] }): JSX.Element {
  if (items.length === 0) {
    return (
      <Text size="sm" data-testid="version-diff-same">
        {t('methodologies.compareSame')}
      </Text>
    );
  }

  const groups = groupDiff(items);

  return (
    <Stack gap="md">
      {DiffKinds.filter((kind) => groups[kind].length > 0).map((kind) => (
        <section key={kind} aria-label={kindLabel(kind)} data-diff-kind={kind}>
          <Text fw={600} mb="xs">
            {kindLabel(kind)}
          </Text>

          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.code')}</Table.Th>
                <Table.Th>{t('methodologies.change')}</Table.Th>
                <Table.Th>{t('methodologies.changedFields')}</Table.Th>
                <Table.Th>{t('methodologies.before')}</Table.Th>
                <Table.Th>{t('methodologies.after')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {groups[kind].map((item) => (
                <Table.Tr key={itemKey(item)} data-change={item.change}>
                  <Table.Td>
                    <Text size="sm">{item.code}</Text>
                    <ConstantScope item={item} />
                  </Table.Td>
                  <Table.Td>
                    <ChangeBadge change={item.change} />
                  </Table.Td>
                  <Table.Td>
                    {item.changedFields.length === 0 ? (
                      '—'
                    ) : (
                      <Group gap="xs">
                        {item.changedFields.map((field) => (
                          <Code key={field}>{field}</Code>
                        ))}
                      </Group>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Value kind={kind} value={item.before} />
                  </Table.Td>
                  <Table.Td>
                    <Value kind={kind} value={item.after} />
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </section>
      ))}
    </Stack>
  );
}

/** Константа — це код + категорія + речовина + дата (`ФВ-16.5`); без них два варіанти не розрізнити. */
function ConstantScope({ item }: { item: MethodologyDiffItem }): JSX.Element | null {
  const parts = [
    item.category === null ? null : `${t('methodologies.category')}: ${item.category}`,
    item.substanceEntryId === null ? null : `#${String(item.substanceEntryId)}`,
    item.validFrom === null ? null : `${t('methodologies.validFrom')}: ${item.validFrom}`,
  ].filter((part): part is string => part !== null);

  if (parts.length === 0) return null;

  return (
    <Text size="xs" c="dimmed">
      {parts.join(' · ')}
    </Text>
  );
}

/** Головне значення; формула — моноширинним, бо в виразі важить кожен символ. */
function Value({ kind, value }: { kind: DiffKind; value: string | null }): JSX.Element {
  if (value === null) return <>—</>;

  return kind === 'Formula' ? (
    <Text size="sm" ff="monospace" data-testid="diff-formula">
      {value}
    </Text>
  ) : (
    <Text size="sm">{value}</Text>
  );
}

const ChangeTones: Readonly<Record<MethodologyDiffItem['change'], StatusTone>> = {
  Added: 'info',
  Removed: 'danger',
  Changed: 'warning',
};

function ChangeBadge({ change }: { change: MethodologyDiffItem['change'] }): JSX.Element {
  const fill = toneFills[ChangeTones[change]];

  return (
    <Badge size="sm" miw="fit-content" variant="default" c={fill.text} bg={fill.bg} data-change-badge={change}>
      {changeLabel(change)}
    </Badge>
  );
}

// ⚠ Кожен ключ — окремим літералом: сторож каталогу (`EndpointCoverageTests`)
// бачить лише літерали, а ключ у змінній вимагав би запису в його перелік.
function changeLabel(change: MethodologyDiffItem['change']): string {
  switch (change) {
    case 'Added':
      return t('methodologies.changeAdded');
    case 'Removed':
      return t('methodologies.changeRemoved');
    case 'Changed':
      return t('methodologies.changeChanged');
  }
}

function kindLabel(kind: DiffKind): string {
  switch (kind) {
    case 'Formula':
      return t('methodologies.formulas');
    case 'Constant':
      return t('methodologies.constants');
    case 'TestCase':
      return t('methodologies.tests');
  }
}

function versionStateLabel(status: MethodologyDraftVersionDto['status']): string {
  switch (status) {
    case 'Published':
      return t('status.version.Published');
    case 'Deprecated':
      return t('status.version.Deprecated');
    default:
      return t('status.version.Draft');
  }
}

function itemKey(item: MethodologyDiffItem): string {
  return [
    item.kind,
    item.code,
    item.category ?? '',
    String(item.substanceEntryId ?? ''),
    item.validFrom ?? '',
  ].join('|');
}
