import type { JSX } from 'react';
import { Alert, Badge, Group, Stack, Table, Text } from '@mantine/core';
import type { ValidationFindingDto } from '@/api/types';
import { t } from '@/shared/i18n';

/** Що показувати в панелі зауважень. */
export interface ValidationPanelProps {
  /**
   * Повідомлення останньої перевірки; `null` — перевірку ще не запускали.
   *
   * ⛔ `null` і `[]` — РІЗНІ стани, і плутати їх не можна: порожній перелік
   * означає «перевірили, зауважень немає», а `null` — «не перевіряли». Зелений
   * напис під документом, якого ніхто не перевіряв, — це та сама неправда, що
   * й порожній дашборд замість збою (`A7-04`).
   */
  messages: readonly ValidationFindingDto[] | null;
}

/**
 * Перелік зауважень перевірки — **на екрані**, а не числом у тості
 * (`ФВ-5.1`, `ФВ-14.24`, директива №09 `W8` п.3, `S-19`).
 *
 * ⛔ Тут була одна нотифікація: «перевірка знайшла N помилок». Число без
 * переліку не веде до жодної дії — оператор дізнавався, що щось не так, і не
 * дізнавався ні що саме, ні де. Сервер при цьому віддавав повний список у тій
 * самій відповіді: `POST …/validate` повертає `messages` з `rowKey`,
 * `columnCode`, кодом правила й текстом. Клієнт брав із нього `length` і
 * викидав решту.
 *
 * ⚠ Адреса (`rowKey` / `columnCode`) — окремими колонками, а не в тексті:
 * саме вона перетворює зауваження на дію. Правило рівня таблиці чи документа
 * адреси рядка не має за визначенням — там стоїть прочерк, а не порожнеча,
 * щоб «немає адреси» не читалося як «не показали».
 */
export function ValidationPanel({ messages }: ValidationPanelProps): JSX.Element | null {
  if (messages === null) {
    return null;
  }

  if (messages.length === 0) {
    return (
      <Alert color="green" variant="light" title={t('document.validationClean')}>
        {t('document.validationCleanHint')}
      </Alert>
    );
  }

  const errors = messages.filter((message) => message.severity === 'Error').length;

  return (
    <Alert
      color={errors === 0 ? 'yellow' : 'red'}
      variant="light"
      title={
        <Group gap="xs">
          <Text fw={600}>{t('document.validationTitle')}</Text>
          <Badge size="sm" color={errors === 0 ? 'yellow' : 'red'}>
            {t('document.validationErrors', { count: errors })}
          </Badge>
        </Group>
      }
    >
      <Stack gap="xs">
        {/* ⚠ Пояснення НАСЛІДКУ, а не переказ числа: оператор має знати, що
            саме `Error` не дасть подати аркуш, а `Warning` — дасть. Без цього
            жовте й червоне виглядають однаково тривожно. */}
        <Text size="sm">{t('document.validationHint')}</Text>

        <Table striped withTableBorder highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('document.validationSeverity')}</Table.Th>
              <Table.Th>{t('document.validationRow')}</Table.Th>
              <Table.Th>{t('document.validationColumn')}</Table.Th>
              <Table.Th>{t('document.validationRule')}</Table.Th>
              <Table.Th>{t('document.validationMessage')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {messages.map((message, index) => (
              <Table.Tr key={`${message.ruleCode}:${message.rowKey ?? ''}:${message.columnCode ?? ''}:${index}`}>
                <Table.Td>
                  <Badge size="sm" color={colorOf(message.severity)} variant="light">
                    {message.severity}
                  </Badge>
                </Table.Td>
                <Table.Td>{message.rowKey ?? '—'}</Table.Td>
                <Table.Td>{message.columnCode ?? '—'}</Table.Td>
                <Table.Td>{message.ruleCode}</Table.Td>
                <Table.Td>{message.message}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Stack>
    </Alert>
  );
}

/** Колір рівня; невідомий рівень лишається нейтральним, а не червоним. */
function colorOf(severity: string): string {
  if (severity === 'Error') return 'red';
  if (severity === 'Warning') return 'yellow';

  return 'blue';
}
