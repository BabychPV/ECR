import type { JSX } from 'react';
import { Badge, Button, Skeleton, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { listValidationRules, validationRulesKey } from './validationRuleApi';

/**
 * Наявні правила валідації таблиці — з кнопкою видалення на кожному (X-15).
 *
 * ⛔ Доти діалог видаляв правило КОДОМ, введеним у текстове поле: перелік
 * правил не віддавав жоден маршрут, і людина мала пам'ятати код, якого екран
 * ніде не показував. Саме видалення підтверджує викликач (`ConfirmModal`).
 *
 * ⚠ Порядок гілок той самий, що в `AsyncBoundary`: `error` → `isPending` →
 * дані; порожній перелік — окреме речення, а не порожня таблиця.
 */
export function ValidationRuleList({
  templateVersionId,
  tableDefId,
  disabled,
  deletingCode,
  onDelete,
}: {
  templateVersionId: number;
  tableDefId: number;
  disabled: boolean;

  /** Код правила, видалення якого зараз іде; `null` — жодного. */
  deletingCode: string | null;
  onDelete: (code: string) => void;
}): JSX.Element {
  const rules = useQuery({
    queryKey: validationRulesKey(templateVersionId, tableDefId),
    queryFn: () => listValidationRules(templateVersionId, tableDefId),
  });

  if (rules.error !== null) {
    return <ErrorAlert error={rules.error} onRetry={() => void rules.refetch()} />;
  }

  if (rules.isPending) {
    return <Skeleton height={80} radius="sm" data-validation-rules="pending" />;
  }

  if (rules.data.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-validation-rules="empty">
        {t('validationRules.empty')}
      </Text>
    );
  }

  return (
    <Table striped withTableBorder data-validation-rules="list">
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t('validationRules.code')}</Table.Th>
          <Table.Th>{t('validationRules.expression')}</Table.Th>
          <Table.Th />
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {rules.data.map((rule) => (
          <Table.Tr key={rule.id}>
            <Table.Td>
              {rule.code}
              {!rule.isActive && (
                <Badge ml="xs" size="xs" variant="outline">
                  {t('validationRules.inactive')}
                </Badge>
              )}
            </Table.Td>
            <Table.Td>
              <Text size="xs" ff="monospace">
                {rule.expression}
              </Text>
            </Table.Td>
            <Table.Td>
              <Button
                size="compact-xs"
                variant="subtle"
                color="statusError"
                disabled={disabled}
                loading={deletingCode === rule.code}
                aria-label={t('validationRules.deleteNamed', { code: rule.code })}
                onClick={() => onDelete(rule.code)}
              >
                {t('validationRules.delete')}
              </Button>
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}
