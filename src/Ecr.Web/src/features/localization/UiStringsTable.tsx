import { memo, type JSX, type RefObject } from 'react';
import { Badge, Button, Group, Table, Text, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';

interface UiStringsTableProps {
  /** Ключі, що намальовано зараз (уже відфільтровані й обрізані порцією). */
  visible: string[];
  /** Чи є ще що домальовувати — тоді останнім рядком стоїть маячок. */
  hasMore: boolean;
  sentinel: RefObject<HTMLDivElement | null>;
  strings: Record<string, string>;
  original: Record<string, string>;
  isDefault: boolean;
  editingKey: string | null;
  draft: string;
  saving: boolean;
  onDraftChange: (value: string) => void;
  onEdit: (key: string, initial: string) => void;
  onCancel: () => void;
  onSave: (key: string) => void;
}

/**
 * Таблиця каталогу рядків (`UiStringsPage`).
 *
 * ⛔ `memo` — частина виправлення живого дефекту (2026-09-24): друк у
 * «Filter by key» перерендерював цю таблицю (до 100 рядків × 4 комірки)
 * СИНХРОННО на кожен символ — максимум 227 мс на символ (dev). Тепер сторінка
 * фільтрує за `useDeferredValue`: термінове оновлення міняє лише поле вводу,
 * а ця таблиця — з незмінними пропсами — не рендериться, доки не прийде
 * відкладений рендер із новим фільтром. Тому всі пропси зі сторінки мають
 * бути стабільними (`useMemo`/`useCallback`).
 */
export const UiStringsTable = memo(function UiStringsTable({
  visible,
  hasMore,
  sentinel,
  strings,
  original,
  isDefault,
  editingKey,
  draft,
  saving,
  onDraftChange,
  onEdit,
  onCancel,
  onSave,
}: UiStringsTableProps): JSX.Element {
  return (
    <Table striped className="ecr-sticky-head">
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t('uiStrings.key')}</Table.Th>
          <Table.Th>{t('uiStrings.original')}</Table.Th>
          <Table.Th>{t('uiStrings.translation')}</Table.Th>
          <Table.Th />
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {visible.map((key) => {
          const value = strings[key] ?? '';
          const source = original[key] ?? '';

          // ⛔ Ознака «немає перекладу»: значення дослівно збігається з
          // оригіналом. Сервер підміняє відсутній переклад мовою за
          // замовчуванням, тому в каталозі порожнеча не видно ніколи —
          // і саме тому неперекладений інтерфейс виглядає перекладеним.
          const untranslated = !isDefault && value === source;

          return (
            <Table.Tr key={key}>
              {/* ⚠ `data-allow-dotted`: ключ каталогу тут — ДАНІ редактора,
                  а не неперекладений напис (сторож `ФВ-14.9`, `D-138`). */}
              <Table.Td data-allow-dotted>
                <Text size="xs">{key}</Text>
              </Table.Td>
              <Table.Td>{source}</Table.Td>
              <Table.Td>
                {editingKey === key ? (
                  <TextInput
                    size="xs"
                    aria-label={`${t('uiStrings.translation')} · ${key}`}
                    value={draft}
                    onChange={(event) => onDraftChange(event.currentTarget.value)}
                    data-autofocus
                  />
                ) : (
                  <Group gap="xs">
                    <Text>{value}</Text>
                    {untranslated && (
                      <Badge size="xs" color="statusWarning" variant="light">
                        {t('uiStrings.untranslated')}
                      </Badge>
                    )}
                  </Group>
                )}
              </Table.Td>
              <Table.Td>
                <Group gap="xs" justify="flex-end">
                  {editingKey === key ? (
                    <>
                      <Button size="compact-xs" variant="subtle" onClick={onCancel}>
                        {t('common.cancel')}
                      </Button>
                      <Button size="compact-xs" loading={saving} onClick={() => onSave(key)}>
                        {t('common.save')}
                      </Button>
                    </>
                  ) : (
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      // ⚠ Поле відкривається з ПОРОЖНІМ значенням для
                      // неперекладеного ключа: підставлений оригінал тут —
                      // найлегший спосіб «перекласти» сотню рядків, натиснувши
                      // «зберегти» сто разів.
                      onClick={() => onEdit(key, untranslated ? '' : value)}
                    >
                      {t('uiStrings.edit')}
                    </Button>
                  )}
                </Group>
              </Table.Td>
            </Table.Tr>
          );
        })}

        {/* ⛔ Маячок — ОСТАННІМ РЯДКОМ таблиці, а не сусіднім блоком:
            `<div>` між `<tbody>` і `</table>` браузер викидає з таблиці
            (foster parenting), і спостерігач стежив би за елементом, що
            стоїть НЕ там, де здається в коді.

            ⚠ Рядок є ЛИШЕ доки є що домальовувати: інакше він лишався б
            порожнім хвостом смугастої таблиці назавжди. */}
        {hasMore && (
          <Table.Tr data-testid="ui-strings-sentinel">
            <Table.Td colSpan={4}>
              <div ref={sentinel} aria-hidden="true" />
            </Table.Td>
          </Table.Tr>
        )}
      </Table.Tbody>
    </Table>
  );
});
