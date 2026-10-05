import { useState, type JSX } from 'react';
import { Anchor, Badge, Group, List, Modal, Skeleton, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import type { RegistryEntryDto } from '@/api/types';
import { todayDateOnly } from '@/shared/format';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import {
  hasNamedReferences,
  loadEntryUsage,
  type EntryUsageReport,
  type FieldReferences,
} from './entryUsage';

/** Адреса переліку записів довідника з пошуком за кодом — там запис і відкривається. */
function entryLink(registryCode: string, entryCode: string): string {
  const params = new URLSearchParams({ code: registryCode, q: entryCode });

  return `/admin/registries?${params.toString()}`;
}

/**
 * Ключ кешу звіту на запис.
 *
 * ⚠ Тут, а не в `api/queryKeys.ts` (спільний файл). Форма під `['registries', …]`, тож
 * `queryKeys.registries.all()` інвалідовує й цей запис — після правки довідника звіт перечитується.
 */
function entryUsageKey(code: string, id: number, asOf: string): readonly unknown[] {
  return ['registries', 'entry-usage', code, id, asOf] as const;
}

export interface EntryUsageModalProps {
  /** Код довідника, якому належить запис. */
  readonly registryCode: string;
  /** Запис; `null` — діалог закритий. */
  readonly entry: RegistryEntryDto | null;
  /** Уже завантажені записи цього довідника — з них беруться дочірні. */
  readonly siblings: readonly RegistryEntryDto[];
  readonly onClose: () => void;
}

/**
 * «Де використовується» запис довідника (ФВ-8.14).
 *
 * ⚠ `export default` — вимога `React.lazy()`: діалог вантажиться окремим чанком лише на клік.
 */
export default function EntryUsageModal({
  registryCode,
  entry,
  siblings,
  onClose,
}: EntryUsageModalProps): JSX.Element {
  return (
    <Modal
      opened={entry !== null}
      onClose={onClose}
      size="lg"
      title={t('registries.entryUsage.title', { code: entry?.code ?? '' })}
    >
      {entry !== null && (
        <EntryUsageBody registryCode={registryCode} entry={entry} siblings={siblings} />
      )}
    </Modal>
  );
}

function EntryUsageBody({
  registryCode,
  entry,
  siblings,
}: {
  registryCode: string;
  entry: RegistryEntryDto;
  siblings: readonly RegistryEntryDto[];
}): JSX.Element {
  // ⚠ Дата фіксується на відкриття діалогу: інакше перехід через північ змінив би ключ і
  // перечитав звіт посеред читання. ⛔ L9-42: саме `useState` — виклик `todayDateOnly()` у тілі
  // рахувався на КОЖЕН рендер і нічого не фіксував.
  const [asOf] = useState(todayDateOnly);

  const report = useQuery({
    queryKey: entryUsageKey(registryCode, entry.id, asOf),
    queryFn: () => loadEntryUsage(registryCode, entry, siblings, asOf),
  });

  // ⛔ Порядок гілок: `error` → `isPending` → дані. Відмова НЕ є «ніде не використано» (`L10`).
  if (report.error !== null) {
    return <ErrorAlert error={report.error} onRetry={() => void report.refetch()} />;
  }

  if (report.isPending) {
    return <Skeleton height={160} radius="sm" data-entry-usage="pending" />;
  }

  return <EntryUsageReportView registryCode={registryCode} report={report.data} asOf={asOf} />;
}

/** Звіт — окремо від запиту, щоб тести перевіряли подання без мережі. */
export function EntryUsageReportView({
  registryCode,
  report,
  asOf,
}: {
  registryCode: string;
  report: EntryUsageReport;
  asOf: string;
}): JSX.Element {
  return (
    <Stack gap="md" data-entry-usage="report">
      {!hasNamedReferences(report) && (
        <Text size="sm" fw={600} data-entry-usage="none-named">
          {t('registries.entryUsage.noneNamed')}
        </Text>
      )}

      <Stack gap="xs">
        <Title order={3} size="h5">
          {t('registries.entryUsage.fieldsTitle')}
        </Title>
        <Text size="xs" c="dimmed">
          {t('registries.entryUsage.asOfNote', { date: asOf })}
        </Text>
        {report.fields.length === 0 ? (
          <Text size="sm" data-entry-usage="fields-none">
            {t('registries.entryUsage.noFields')}
          </Text>
        ) : (
          report.fields.map((group) => (
            <FieldGroup key={`${group.field.registryCode}.${group.field.fieldCode}`} group={group} />
          ))
        )}
      </Stack>

      <Stack gap="xs">
        <Title order={3} size="h5">
          {t('registries.entryUsage.childrenTitle')}
        </Title>
        {report.children.length === 0 ? (
          <Text size="sm" data-entry-usage="children-none">
            {t('registries.entryUsage.none')}
          </Text>
        ) : (
          <List listStyleType="none" spacing="xs" data-entry-usage="children">
            {report.children.map((child) => (
              <List.Item key={child.id}>
                <Anchor component={Link} to={entryLink(registryCode, child.code)} size="sm">
                  {child.code}
                </Anchor>{' '}
                <Text span size="sm" c="dimmed">
                  {child.display}
                </Text>
              </List.Item>
            ))}
          </List>
        )}
      </Stack>

      <Stack gap="xs">
        <Title order={3} size="h5">
          {t('registries.entryUsage.substancesTitle')}
        </Title>
        {report.substances.length === 0 ? (
          <Text size="sm" data-entry-usage="substances-none">
            {t('registries.entryUsage.none')}
          </Text>
        ) : (
          <List listStyleType="none" spacing="xs" data-entry-usage="substances">
            {report.substances.map((substance) => (
              <List.Item key={substance.id}>
                {substance.route === null ? (
                  <Text size="sm">{t('registries.entryUsage.substanceLink')}</Text>
                ) : (
                  <Anchor component={Link} to={substance.route} size="sm">
                    {t('registries.entryUsage.substanceLink')}
                  </Anchor>
                )}{' '}
                <Text span size="xs" c="dimmed">
                  {substance.id}
                </Text>
              </List.Item>
            ))}
          </List>
        )}
      </Stack>

      {(report.columns.length > 0 || report.dataInDocuments) && (
        <Stack gap="xs">
          <Title order={3} size="h5">
            {t('registries.entryUsage.columnsTitle')}
          </Title>
          <Text size="xs" c="dimmed">
            {t('registries.entryUsage.columnsHint')}
          </Text>
          {report.dataInDocuments && (
            <Text size="sm" data-entry-usage="data">
              {t('registries.usageDataInDocuments')}
            </Text>
          )}
          <List listStyleType="none" spacing="xs" data-entry-usage="columns">
            {report.columns.map((column) => (
              <List.Item key={column.id}>
                {column.route === null ? (
                  <Text size="sm">{column.label}</Text>
                ) : (
                  <Anchor component={Link} to={column.route} size="sm">
                    {column.label}
                  </Anchor>
                )}
              </List.Item>
            ))}
          </List>
        </Stack>
      )}

      {report.truncated && (
        <Text size="sm" c="statusWarning" data-entry-usage="truncated">
          {t('registries.entryUsage.truncated')}
        </Text>
      )}

      {/* ⛔ Прямо названа межа звіту: ці види посилань сервер рахує (відмова видалення), але
          поіменно на запис не перелічує. Мовчанка тут читалася б як «їх немає». */}
      <Text size="sm" c="dimmed" data-entry-usage="not-listed">
        {t('registries.entryUsage.notListed')}
      </Text>
    </Stack>
  );
}

function FieldGroup({ group }: { group: FieldReferences }): JSX.Element {
  const { field } = group;

  return (
    <Stack gap="xs" data-entry-usage-field={`${field.registryCode}.${field.fieldCode}`}>
      <Group gap="xs">
        {field.route === null ? (
          <Text size="sm" fw={600}>
            {field.registryCode}
          </Text>
        ) : (
          <Anchor component={Link} to={field.route} size="sm" fw={600}>
            {field.registryCode}
          </Anchor>
        )}
        <Badge size="xs" variant="light">
          {field.fieldCode}
        </Badge>
        {group.status === 'ok' && (
          <Text size="xs" c="dimmed">
            {t('registries.entryUsage.fieldCount', { count: group.total })}
          </Text>
        )}
      </Group>

      {group.status === 'error' ? (
        <ErrorAlert error={group.error} />
      ) : group.rows.length === 0 ? (
        <Text size="sm">{t('registries.entryUsage.none')}</Text>
      ) : (
        <>
          <List listStyleType="none" spacing="xs">
            {group.rows.map((row) => (
              <List.Item key={row.id}>
                <Anchor component={Link} to={entryLink(field.registryCode, row.code)} size="sm">
                  {row.code}
                </Anchor>{' '}
                <Text span size="sm" c="dimmed">
                  {row.display}
                </Text>
              </List.Item>
            ))}
          </List>
          {group.total > group.rows.length && (
            <Text size="xs" c="dimmed" data-entry-usage="field-truncated">
              {t('registries.entryUsage.fieldShown', { shown: group.rows.length, total: group.total })}
            </Text>
          )}
        </>
      )}
    </Stack>
  );
}
