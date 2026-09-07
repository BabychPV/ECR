import type { JSX } from 'react';
import { Badge, Stack, Table, Text, Title } from '@mantine/core';
import type { MappingPreview } from '@/api/types';
import { brokenMaps, hasNoGaps } from './api';
import { outcomeLabel } from './outcome';
import { t } from '@/shared/i18n';

/**
 * Розриви мапінгу — головна частина перегляду (`ФВ-13.14`).
 *
 * ⛔ Розриви стоять **перед** списком успішних зв'язків, а не після. Перегляд,
 * який починається з того, що зійшлося, відповідає на питання, якого ніхто не
 * ставить: у справний мапінг не заглядають. Відкривають його тоді, коли числа
 * не ті, — і тоді потрібні три відповіді: що не лягає нікуди, під що немає
 * даних і за якою колонкою не стоїть нічого.
 *
 * ⚠ Порожній перелік розривів теж має вигляд: без явного «розривів немає»
 * порожнеча читалася б як «перевірка не відпрацювала» (`ФВ-14.22`).
 */
export function MappingGaps({ preview }: { readonly preview: MappingPreview }): JSX.Element {
  const broken = brokenMaps(preview.fields);

  if (hasNoGaps(preview)) {
    return (
      <Stack gap="xs" mb="lg">
        <Title order={2} size="h4">
          {t('mapping.gaps')}
        </Title>
        <Text c="dimmed">{t('mapping.noGaps')}</Text>
      </Stack>
    );
  }

  return (
    <Stack gap="lg" mb="lg">
      <Title order={2} size="h4">
        {t('mapping.gaps')}
      </Title>

      {broken.length > 0 && (
        <Stack gap="xs">
          <Title order={3} size="h5">
            {t('mapping.broken')}
          </Title>

          {/* ⛔ Найдорожчий рядок переліку. `NoData` — це друкарська помилка в
              шляху AF: збір «успішний», точок нуль, і в журналі прогонів він
              не відрізняється від справного (ІНТ-3.3). */}
          <Text size="sm" c="dimmed">
            {t('mapping.brokenHint')}
          </Text>

          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('mapping.field')}</Table.Th>
                <Table.Th>{t('mapping.target')}</Table.Th>
                <Table.Th>{t('mapping.outcome')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {broken.map((field) => (
                <Table.Tr key={field.fieldMapId}>
                  <Table.Td>{field.sourceField}</Table.Td>
                  <Table.Td>{target(field.targetRowKey, field.targetColumnCode)}</Table.Td>
                  <Table.Td>
                    <Badge color="red" variant="light">
                      {outcomeLabel(field.outcome)}
                    </Badge>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Stack>
      )}

      {preview.unmappedSourceFields.length > 0 && (
        <Stack gap="xs">
          <Title order={3} size="h5">
            {t('mapping.unmapped')}
          </Title>
          <Text size="sm" c="dimmed">
            {t('mapping.unmappedHint')}
          </Text>

          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('mapping.field')}</Table.Th>
                <Table.Th>{t('mapping.points')}</Table.Th>
                <Table.Th>{t('mapping.lastSeen')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {preview.unmappedSourceFields.map((field) => (
                <Table.Tr key={field.sourcePath}>
                  <Table.Td>{field.sourcePath}</Table.Td>
                  <Table.Td>{field.pointCount}</Table.Td>
                  <Table.Td>{field.lastSeenUtc}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Stack>
      )}

      {preview.uncoveredColumns.length > 0 && (
        <Stack gap="xs">
          <Title order={3} size="h5">
            {t('mapping.uncovered')}
          </Title>

          {/* ⚠ «Заповнюється людиною» — законна відповідь. Колонка, у яку не
              можна ані ввести руками, ані порахувати, значення не отримає
              ніколи, і саме вона стоїть у переліку першою. */}
          <Text size="sm" c="dimmed">
            {t('mapping.uncoveredHint')}
          </Text>

          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('mapping.column')}</Table.Th>
                <Table.Th>{t('mapping.fill')}</Table.Th>
                <Table.Th>{t('mapping.required')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {preview.uncoveredColumns.map((column) => (
                <Table.Tr key={column.columnDefId}>
                  <Table.Td>
                    {column.code}
                    <Text size="xs" c="dimmed">
                      {column.header ?? column.code}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Badge color={column.isUnfillable ? 'red' : 'gray'} variant="light">
                      {/* ⚠ Два виклики `t`, а не один із тернарним ключем:
                          зібраний ключ невидимий для сторожа каталогу
                          (`D2-172`). */}
                      {column.isUnfillable ? t('mapping.unfillable') : t('mapping.manual')}
                    </Badge>
                  </Table.Td>
                  <Table.Td>{column.isRequired ? t('mapping.yes') : t('mapping.no')}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Stack>
      )}
    </Stack>
  );
}

/** Адреса призначення одним рядком; `—`, якщо адреси немає. */
export function target(rowKey: string | null, columnCode: string | null): string {
  if (rowKey === null && columnCode === null) {
    return '—';
  }

  return `${rowKey ?? '?'} · ${columnCode ?? '?'}`;
}
