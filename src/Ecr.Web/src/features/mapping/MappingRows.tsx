import type { JSX } from 'react';
import { Badge, Stack, Table, Text, Title } from '@mantine/core';
import type { MappingPreview } from '@/api/types';
import { target } from './MappingGaps';
import { outcomeColor, outcomeLabel } from './outcome';
import { t } from '@/shared/i18n';

/**
 * Мапінги і реальні рядки джерела (`ФВ-13.14`).
 *
 * ⛔ Спершу — що ляже в комірку, потім — рядки, з яких це число вийшло.
 * Порядок не косметичний: питання «чому в комірці саме це» без згорнутого
 * значення поруч із серією неможливо закрити, не викачуючи дані руками.
 *
 * ⚠ Значення показуються **в одиниці ДЖЕРЕЛА** — так вони й зберігаються
 * (`ФВ-16.10`, `D-79`). Тому одиниці стоять обидві: розбіжність між ними і є
 * найчастіше джерело мовчазних розходжень у числах (`ФВ-16.9`).
 */
export function MappingRows({ preview }: { readonly preview: MappingPreview }): JSX.Element {
  return (
    <Stack gap="lg">
      <Stack gap="xs">
        <Title order={2} size="h4">
          {t('mapping.maps')}
        </Title>

        <Table striped highlightOnHover className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('mapping.field')}</Table.Th>
              <Table.Th>{t('mapping.target')}</Table.Th>
              <Table.Th>{t('mapping.aggregation')}</Table.Th>
              <Table.Th>{t('mapping.units')}</Table.Th>
              <Table.Th>{t('mapping.points')}</Table.Th>
              <Table.Th>{t('mapping.folded')}</Table.Th>
              <Table.Th>{t('mapping.outcome')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {preview.fields.map((field) => (
              <Table.Tr key={field.fieldMapId}>
                <Table.Td>{field.sourceField}</Table.Td>
                <Table.Td>{target(field.targetRowKey, field.targetColumnCode)}</Table.Td>
                <Table.Td>{field.aggregation ?? '—'}</Table.Td>
                <Table.Td>
                  {field.sourceUnitCode ?? '—'} → {field.targetUnitCode ?? '—'}
                </Table.Td>
                <Table.Td>{field.pointCount}</Table.Td>
                <Table.Td>{field.foldedValue ?? '—'}</Table.Td>
                <Table.Td>
                  <Badge color={outcomeColor(field.outcome)} variant="light">
                    {outcomeLabel(field.outcome)}
                  </Badge>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Stack>

      <Stack gap="xs">
        <Title order={2} size="h4">
          {t('mapping.rows')}
        </Title>
        <Text size="sm" c="dimmed">
          {t('mapping.rowsHint')}
        </Text>

        <Table striped highlightOnHover className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('mapping.timestamp')}</Table.Th>
              <Table.Th>{t('mapping.field')}</Table.Th>
              <Table.Th>{t('mapping.value')}</Table.Th>
              <Table.Th>{t('mapping.quality')}</Table.Th>
              <Table.Th>{t('mapping.target')}</Table.Th>
              <Table.Th>{t('mapping.outcome')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {preview.rows.map((row, index) => (
              <Table.Tr key={`${row.sourcePath}|${row.timestamp}|${index}`}>
                <Table.Td>{row.timestamp}</Table.Td>
                <Table.Td>{row.sourcePath}</Table.Td>
                <Table.Td>{row.valueNumeric ?? row.valueString ?? '—'}</Table.Td>
                <Table.Td>{row.quality ?? '—'}</Table.Td>
                <Table.Td>{target(row.targetRowKey, row.targetColumnCode)}</Table.Td>
                <Table.Td>
                  <Badge color={outcomeColor(row.outcome)} variant="light">
                    {outcomeLabel(row.outcome)}
                  </Badge>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Stack>
    </Stack>
  );
}
