import type { JSX } from 'react';
import { Badge, Group, Stack, Table, Text } from '@mantine/core';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import { piValueText, type PiEventAttributeView } from './eventsApi';
import { comparisonColor, comparisonLabel, reasonLabel } from './piEventLabels';

/**
 * Атрибути події PI (`D-465`): що синк прочитав з PI востаннє, що він записав би в колонку (тим самим правилом, що
 * пише), що зараз у комірці — і порівняння з причиною.
 *
 * ⚠ Значення комірки сервер віддає лише тому, хто бачить документ і колонку; інакше — «Приховано».
 */
export function PiEventAttributes({
  attributes,
  caption,
}: {
  readonly attributes: readonly PiEventAttributeView[];
  readonly caption: string;
}): JSX.Element {
  return (
    <Table.ScrollContainer minWidth={720}>
      <Table striped withTableBorder aria-label={caption} data-pi-event-attributes="">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t('piEvents.attribute')}</Table.Th>
            <Table.Th scope="col">{t('piEvents.piValue')}</Table.Th>
            <Table.Th scope="col">{t('piEvents.expected')}</Table.Th>
            <Table.Th scope="col">{t('piEvents.cellValue')}</Table.Th>
            <Table.Th scope="col">{t('piEvents.comparison')}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {attributes.map((attribute) => (
            <Table.Tr
              key={`${attribute.scope}:${attribute.attribute}:${String(attribute.columnDefId ?? '')}`}
              data-pi-event-attribute={attribute.attribute}
            >
              <Table.Td>
                <Text size="sm" ff="monospace">
                  {attribute.attribute}
                </Text>
                <Text size="xs" c="dimmed">
                  {attribute.columnCode === null ? t('piEvents.notMapped') : `→ ${attribute.columnCode}`}
                  {attribute.scope === 'PrimaryElement' ? ` · ${t('sourceEvents.scopePrimaryElement')}` : ''}
                </Text>
              </Table.Td>
              <Table.Td>
                <Text size="sm" ff="monospace" data-pi-event-pi-value="">
                  {piValueText(attribute)}
                  {attribute.piUnit === null ? '' : ` ${attribute.piUnit}`}
                </Text>
                {attribute.piValueTruncated && (
                  <Text size="xs" c="dimmed">
                    {t('piEvents.truncated')}
                  </Text>
                )}
                {attribute.piChangedAt !== null && (
                  <Text size="xs" c="dimmed">
                    {t('piEvents.readAt')} <Timestamp value={attribute.piChangedAt} />
                  </Text>
                )}
              </Table.Td>
              <Table.Td>
                <Text size="sm" ff="monospace" data-pi-event-expected="">
                  {attribute.expectedValue ?? '—'}
                </Text>
              </Table.Td>
              <Table.Td>
                <Text size="sm" ff="monospace" data-pi-event-cell="">
                  {attribute.cellValue ?? '—'}
                </Text>
                {attribute.cellOrigin !== null && (
                  <Text size="xs" c="dimmed">
                    {attribute.cellOrigin}
                  </Text>
                )}
              </Table.Td>
              <Table.Td>
                <ComparisonCell attribute={attribute} />
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** Бейдж порівняння і причина текстом (не лише кольором — доступність). */
export function ComparisonCell({ attribute }: { readonly attribute: PiEventAttributeView }): JSX.Element | null {
  if (attribute.comparison === null) return null;

  return (
    <Stack gap="xs">
      <Group gap="xs" wrap="nowrap">
        <Badge
          size="sm"
          variant="light"
          color={comparisonColor(attribute.comparison)}
          data-pi-event-comparison={attribute.comparison}
        >
          {comparisonLabel(attribute.comparison)}
        </Badge>
      </Group>
      {attribute.reason !== null && (
        <Text size="xs" c="dimmed" data-pi-event-reason={attribute.reason}>
          {reasonLabel(attribute.reason)}
        </Text>
      )}
    </Stack>
  );
}
