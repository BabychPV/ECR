import type { JSX } from 'react';
import { Alert, Anchor, Badge, Group, Stack, Table, Text } from '@mantine/core';
import type { ValidationFindingDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { ruleLabel } from './ruleLabel';

/** Що показувати в панелі зауважень. */
interface ValidationPanelProps {
  /**
   * Повідомлення останньої перевірки; `null` — перевірку ще не запускали.
   *
   * ⛔ `null` і `[]` — РІЗНІ стани, і плутати їх не можна: порожній перелік
   * означає «перевірили, зауважень немає», а `null` — «не перевіряли». Зелений
   * напис під документом, якого ніхто не перевіряв, — це та сама неправда, що
   * й порожній дашборд замість збою (`A7-04`).
   */
  messages: readonly ValidationFindingDto[] | null;

  /**
   * Перехід до адреси зауваження в сітці (`ФВ-5.6`); без нього текст
   * зауваження — звичайний текст.
   */
  onSelect?: (message: ValidationFindingDto) => void;

  /**
   * AN-39/L8-21: чи веде зауваження до наявної адреси. Без адреси (таблиці немає на екрані)
   * кнопка була б зайвим фокус-стопом, що нічого не робить, - тоді лишається текст.
   */
  canSelect?: ((message: ValidationFindingDto) => boolean) | undefined;
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
 *
 * ⚠ Повна адреса починається з `tableDefId` (`BE-04`): аркуш містить кілька
 * таблиць, і `rowKey` унікальний лише всередині своєї. Поле вже приходить із
 * сервера і входить у ключ рядка.
 *
 * ✎ `ФВ-5.6`: текст зауваження — кнопка переходу до його адреси: аркуш,
 * таблиця, рядок, колонка (`onSelect`, `features/grid/cellNavigation.ts`).
 * Кнопка, а не клік по рядку таблиці: рядок `<tr>` не отримує фокуса з
 * клавіатури, і перехід лишився б лише для миші.
 */
export function ValidationPanel({ messages, onSelect, canSelect }: ValidationPanelProps): JSX.Element | null {
  if (messages === null) {
    return null;
  }

  if (messages.length === 0) {
    return (
      <Alert color="statusSuccess" variant="light" title={t('document.validationClean')}>
        {t('document.validationCleanHint')}
      </Alert>
    );
  }

  const errors = messages.filter((message) => message.severity === 'Error').length;

  return (
    <Alert
      color={errors === 0 ? 'statusWarning' : 'statusError'}
      variant="light"
      title={
        <Group gap="xs">
          <Text fw={600}>{t('document.validationTitle')}</Text>
          <Badge size="sm" color={errors === 0 ? 'statusWarning' : 'statusError'}>
            {/* ⚠ `A2-10`: нуль помилок (лише попередження) — «помилок не знайдено»,
                а не «перевірка виявила помилки: 0». */}
            {errors === 0 ? t('document.validationNoErrors') : t('document.validationErrors', { count: errors })}
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
              <Table.Tr
                key={`${message.tableDefId}:${message.ruleCode}:${message.rowKey ?? ''}:${message.columnCode ?? ''}:${index}`}
              >
                <Table.Td>
                  {/*
                   * ⛔ Тут стояла власна `colorOf(severity)` — шістнадцята за
                   * ліком локальна таблиця кольору статусу, і з тією самою
                   * вадою, що й решта: невідомий рівень отримував `'blue'`,
                   * тобто новий рівень із сервера виглядав би як `Info`.
                   * Набір дає невідомому `warning` — «я цього не знаю» помітно,
                   * а не тихо.
                   *
                   * ⚠ І текст: `{message.severity}` друкував код сервера
                   * (`Error`, `Warning`) англійською на будь-якій мові
                   * інтерфейсу. Підпис тепер із каталогу — `status.severity.*`.
                   */}
                  <StatusBadge kind="severity" state={message.severity} />
                </Table.Td>
                <Table.Td>{message.rowKey ?? '—'}</Table.Td>
                <Table.Td>{message.columnCode ?? '—'}</Table.Td>
                <Table.Td title={message.ruleCode}>{message.displayCode ?? ruleLabel(message.ruleCode)}</Table.Td>
                <Table.Td>
                  {onSelect === undefined || canSelect?.(message) === false ? (
                    message.message
                  ) : (
                    <Anchor
                      component="button"
                      type="button"
                      size="sm"
                      ta="start"
                      title={t('document.validationGoTo')}
                      onClick={() => onSelect(message)}
                    >
                      {message.message}
                    </Anchor>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Stack>
    </Alert>
  );
}

/*
 * ✎ Тут була `colorOf(severity)`. Її прибрано цілком, а не поправлено:
 * директива №15 §2 вимагає, щоб «іншого способу намалювати статус у
 * застосунку не лишалося», і локальна таблиця на три рядки — це саме той
 * другий спосіб, який розходиться з першим мовчки. Розподіл тепер один на
 * застосунок — `statusTable.severity` у `shared/ui/StatusBadge.tsx`.
 */
