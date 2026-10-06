import type { JSX } from 'react';
import { Badge, Button, Group, Skeleton, Table, Text } from '@mantine/core';
import type { UseQueryResult } from '@tanstack/react-query';
import { dataTypeLabel } from '@/features/templates/enumLabels';
import type { HeaderFieldDefDto } from '@/features/templates/headerField';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';

/**
 * Поля шапки документа (рівень усього документа, не таблиці).
 *
 * ⚠ Винесено з `TemplateVersionPage.tsx` без зміни поведінки. Той самий
 * draft→publish контракт, що колонки (`W5.2`), тож та сама заморожена
 * структура (`canEdit` — прапорець `isEditable`) забороняє й тут.
 */
export function HeaderFieldsSection({
  fields,
  canEdit,
  onAdd,
  onEdit,
}: {
  readonly fields: UseQueryResult<readonly HeaderFieldDefDto[]>;
  readonly canEdit: boolean;
  readonly onAdd: () => void;
  readonly onEdit: (field: HeaderFieldDefDto) => void;
}): JSX.Element {
  return (
    <>
      <Group justify="space-between" mb="xs" mt="md">
        <Text fw={600}>{t('headerFields.title')}</Text>
        {canEdit && (
          <Button variant="default" onClick={onAdd}>
            {t('headerFields.add')}
          </Button>
        )}
      </Group>

      {fields.error !== null && <ErrorAlert error={fields.error} onRetry={() => void fields.refetch()} />}

      {fields.error === null && fields.isPending && <Skeleton height={80} radius="sm" mb="sm" />}

      {fields.error === null &&
        fields.data !== undefined &&
        (fields.data.length === 0 ? (
          <Text size="sm" c="dimmed" mb="sm">
            {t('headerFields.empty')}
          </Text>
        ) : (
          <Table striped withTableBorder mb="md">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('headerFields.label')}</Table.Th>
                <Table.Th>{t('headerFields.dataType')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {[...fields.data]
                .sort((a, b) => a.ordinal - b.ordinal)
                .map((field) => (
                  <Table.Tr key={field.id}>
                    <Table.Td>
                      {localized(field.labelL10n) || field.code}{' '}
                      <Text span c="dimmed">
                        ({field.code})
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      {dataTypeLabel(field.dataType)}
                      {field.isRequired && (
                        <Badge ml="xs" size="xs" variant="light">
                          {t('headerFields.required')}
                        </Badge>
                      )}
                    </Table.Td>
                    <Table.Td>
                      {canEdit && (
                        <Group gap="xs" wrap="nowrap" justify="flex-end">
                          <Button size="xs" variant="subtle" onClick={() => onEdit(field)}>
                            {t('headerFields.edit')}
                          </Button>
                        </Group>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
            </Table.Tbody>
          </Table>
        ))}
    </>
  );
}
