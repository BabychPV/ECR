import { useState, type JSX } from 'react';
import {
  Alert,
  Badge,
  Group,
  ScrollArea,
  Skeleton,
  Stack,
  Table,
  Text,
  TextInput,
  useComputedColorScheme,
} from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { TableDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { themeSurface } from '@/shared/theme/theme';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { ConditionalRule } from './conditionalFormat';
import type { ConditionalFormatRuleDto } from './conditionalFormatApi';
import { dataTypeLabel, rowKindLabel } from './enumLabels';
import { buildTablePreview, cellLook, previewCellLook, type PreviewColumn } from './tablePreview';

/**
 * Ключ правил версії для перегляду. ⚠ Локальний і окремий від ключа редактора
 * правил: перегляд читає лише тіло (масив правил) і монтується наново з
 * кожним відкриттям діалогу, тож бачить щойно збережене.
 */
export function tablePreviewRulesKey(templateVersionId: number): readonly ['templates', 'tablePreviewRules', number] {
  return ['templates', 'tablePreviewRules', templateVersionId] as const;
}

/**
 * Правила версії — тіло `GET …/conditional-formats`. ⚠ Свій виклик, а не
 * `getConditionalFormats`: перегляду не потрібна версія набору (`ETag`), а
 * лише масив правил, форма якого в контракті стала.
 */
function loadRules(templateVersionId: number): Promise<ConditionalFormatRuleDto[]> {
  return apiFetch<ConditionalFormatRuleDto[]>(
    `/api/v1/template-versions/${String(templateVersionId)}/conditional-formats`,
  );
}

/** Коротко про правило. ⚠ Ключі — літерали (сторож каталогу бачить їх у коді). */
function ruleSummary(rule: ConditionalRule): string {
  switch (rule.operator) {
    case 'gt':
      return `${t('conditionalFormat.op.gt')} ${rule.value}`;
    case 'ge':
      return `${t('conditionalFormat.op.ge')} ${rule.value}`;
    case 'lt':
      return `${t('conditionalFormat.op.lt')} ${rule.value}`;
    case 'le':
      return `${t('conditionalFormat.op.le')} ${rule.value}`;
    case 'eq':
      return `${t('conditionalFormat.op.eq')} ${rule.value}`;
    case 'ne':
      return `${t('conditionalFormat.op.ne')} ${rule.value}`;
    case 'between':
      return `${t('conditionalFormat.op.between')} ${rule.value} – ${rule.valueTo}`;
    case 'empty':
      return t('conditionalFormat.op.empty');
    case 'notEmpty':
      return t('conditionalFormat.op.notEmpty');
  }
}

function ColumnRules({ column, surface }: { column: PreviewColumn; surface: string }): JSX.Element {
  if (column.rules.length === 0) {
    return (
      <Text size="xs" c="dimmed">
        {t('tablePreview.noRules')}
      </Text>
    );
  }

  return (
    <Stack gap="xs" data-preview-rules={column.code}>
      {column.rules.map((rule, index) => (
        <Text key={index} size="xs" px="xs" style={cellLook(rule, surface)}>
          {ruleSummary(rule)}
        </Text>
      ))}
    </Stack>
  );
}

/**
 * Попередній перегляд таблиці шаблону (`ФВ-2.6`): порядок колонок і рядків,
 * типи, одиниці та умовне форматування (`ФВ-2.7`) — так, як їх покаже
 * документ. ⛔ Лише читання: нічого не зберігає.
 *
 * Значення-приклад підставляється в кожну комірку, і кожна колонка фарбує його
 * першим своїм правилом, що спрацювало, — так видно, як правила ляжуть на
 * реальні дані, без документа.
 *
 * ⚠ Лінивий чанк: сторінка версії вантажить його лише при відкритті діалогу.
 */
export function TablePreview({
  templateVersionId,
  table,
}: {
  readonly templateVersionId: number;
  readonly table: Pick<TableDto, 'columns' | 'rows' | 'layoutKind'>;
}): JSX.Element {
  const [sample, setSample] = useState('');
  const scheme = useComputedColorScheme('light');
  const surface = themeSurface[scheme].body;

  const rules = useQuery({
    queryKey: tablePreviewRulesKey(templateVersionId),
    queryFn: () => loadRules(templateVersionId),
  });

  if (rules.error !== null) {
    return <ErrorAlert error={rules.error} onRetry={() => void rules.refetch()} />;
  }

  if (rules.isPending) {
    return <Skeleton height={160} radius="sm" data-table-preview="pending" />;
  }

  const model = buildTablePreview(table, rules.data, localized);
  const value = sample.trim().length === 0 ? null : sample;

  return (
    <Stack gap="sm">
      <Text size="sm" c="dimmed">
        {t('tablePreview.hint')}
      </Text>

      <TextInput
        label={t('tablePreview.sample')}
        description={t('tablePreview.sampleHint')}
        value={sample}
        onChange={(event) => setSample(event.currentTarget.value)}
      />

      {table.layoutKind === 'MonthsInColumns' && <Alert color="statusInfo">{t('tablePreview.monthsInColumns')}</Alert>}
      {table.layoutKind === 'MonthsInRows' && <Alert color="statusInfo">{t('tablePreview.monthsInRows')}</Alert>}

      {model.columns.length === 0 ? (
        <Text size="sm">{t('tablePreview.noColumns')}</Text>
      ) : (
        <ScrollArea type="auto">
          <Table withTableBorder withColumnBorders aria-label={t('tablePreview.tableLabel')}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th rowSpan={3}>{t('tablePreview.row')}</Table.Th>
                {model.columns.map((column) => (
                  <Table.Th key={column.code} data-preview-column={column.code}>
                    {column.label}
                    {column.isRequired && (
                      <Text span c="statusError" aria-label={t('tablePreview.required')}>
                        {' *'}
                      </Text>
                    )}
                  </Table.Th>
                ))}
              </Table.Tr>
              <Table.Tr>
                {model.columns.map((column) => (
                  <Table.Th key={column.code} fw="normal">
                    <Group gap="xs" wrap="nowrap">
                      <Text size="xs" c="dimmed">
                        {dataTypeLabel(column.dataType)}
                        {column.unitSymbol === null ? '' : `, ${column.unitSymbol}`}
                      </Text>
                      {column.isReadOnly && (
                        <Badge size="xs" variant="light">
                          {t('tablePreview.readOnly')}
                        </Badge>
                      )}
                    </Group>
                  </Table.Th>
                ))}
              </Table.Tr>
              <Table.Tr>
                {model.columns.map((column) => (
                  <Table.Th key={column.code} fw="normal">
                    <ColumnRules column={column} surface={surface} />
                  </Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {model.rows.length === 0 ? (
                <Table.Tr>
                  <Table.Td>
                    <Text size="xs" c="dimmed">
                      {t('tablePreview.newRow')}
                    </Text>
                  </Table.Td>
                  {model.columns.map((column) => (
                    <Table.Td key={column.code} style={previewCellLook(column, value, surface)}>
                      {value ?? ''}
                    </Table.Td>
                  ))}
                </Table.Tr>
              ) : (
                model.rows.map((row) => (
                  <Table.Tr key={row.rowKey} data-preview-row={row.rowKey}>
                    <Table.Td
                      style={{ paddingInlineStart: `calc(var(--mantine-spacing-md) * ${String(row.depth + 1)})` }}
                    >
                      <Text size="sm">{row.label}</Text>
                      <Text size="xs" c="dimmed">
                        {rowKindLabel(row.rowKind)}
                      </Text>
                    </Table.Td>
                    {model.columns.map((column) => (
                      <Table.Td
                        key={column.code}
                        data-preview-cell={`${row.rowKey}:${column.code}`}
                        style={previewCellLook(column, value, surface)}
                      >
                        {value ?? ''}
                      </Table.Td>
                    ))}
                  </Table.Tr>
                ))
              )}
            </Table.Tbody>
          </Table>
        </ScrollArea>
      )}

      {model.rows.length === 0 && model.columns.length > 0 && (
        <Text size="xs" c="dimmed">
          {t('tablePreview.noRows')}
        </Text>
      )}
      {model.hiddenColumns > 0 && (
        <Text size="xs" c="dimmed">
          {t('tablePreview.hiddenColumns', { count: model.hiddenColumns })}
        </Text>
      )}
      {model.truncatedRows > 0 && (
        <Text size="xs" c="dimmed">
          {t('tablePreview.truncated', { shown: model.rows.length, more: model.truncatedRows })}
        </Text>
      )}
      {model.ignoredRules > 0 && (
        <Text size="xs" c="dimmed">
          {t('tablePreview.ignoredRules', { count: model.ignoredRules })}
        </Text>
      )}
    </Stack>
  );
}
