import type { JSX } from 'react';
import { Alert, Button, Group, List, Text } from '@mantine/core';
import type { CellConflictDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { cellText } from './cellValue';
import { conflictTimeLabel } from './useCellPatch';

/** Конфлікт версії, який людина ще не розв'язала (`B-09`). */
export interface OpenConflict {
  /** Розбіжні комірки, як їх назвав сервер (`ECR-CELL-0409`). */
  readonly conflicts: readonly CellConflictDto[];

  /** Скільки розбіжностей сервер не вмістив у перелік (стеля — 100). */
  readonly more: number;
}

/** Властивості панелі конфлікту. */
export interface ConflictPanelProps {
  readonly conflict: OpenConflict;

  /** Підпис рядка, яким його бачить людина (`label`, інакше ключ). */
  readonly rowLabel: (rowKey: string) => string;

  /** Заголовок колонки; невідома колонка — її код. */
  readonly columnHeader: (columnCode: string) => string;

  /** Чи йде зараз збереження — кнопки чекають його кінця. */
  readonly busy: boolean;

  /** Повторити МОЇ значення з чинною версією рядка. */
  readonly onKeepMine: () => void;

  /** Викинути мої значення й показати чинні. */
  readonly onDiscardMine: () => void;
}

/**
 * Розбіжність версій: моє значення проти чинного — і два виходи (`B-09`).
 *
 * ⛔ Доти панель показувала лише ЧУЖЕ значення, а виходу з конфлікту не було
 * жодного: «Retry save» слав ту саму правку з тією самою старою версією рядка,
 * сервер знову відповідав `409` — і так вічно. Людина бачила «хтось змінив ці
 * комірки» без способу ні наполягти на своєму, ні погодитись із чужим.
 *
 * ⚠ «Перезаписати мовчки» так і не стало опцією: «Keep mine» — свідома дія
 * людини, яка щойно побачила обидва значення поруч. Версію для повтору дає
 * сервер (`conflicts[].currentVersion`), а не клієнт.
 *
 * ⚠ Рядок, якого більше немає (`columnCode === '*'`, версія порожня),
 * наполягти не дає: писати нема куди. Для нього лишається «Discard».
 */
export function ConflictPanel({
  conflict,
  rowLabel,
  columnHeader,
  busy,
  onKeepMine,
  onDiscardMine,
}: ConflictPanelProps): JSX.Element {
  const canKeep = conflict.conflicts.some(hasCurrentVersion);

  return (
    <Alert color="statusWarning" title={t('grid.conflictTitle')} data-testid="grid-conflict">
      <Text size="sm">{t('grid.conflictHint', { count: conflict.conflicts.length })}</Text>

      <List size="sm" my="xs">
        {conflict.conflicts.map((item) => (
          <List.Item key={`${item.rowKey}:${item.columnCode}`} data-conflict-cell={`${item.rowKey}:${item.columnCode}`}>
            {!hasCurrentVersion(item)
              ? t('grid.conflictRowGone', { row: rowLabel(item.rowKey) })
              : t('grid.conflictItem', {
                  row: rowLabel(item.rowKey),
                  column: columnHeader(item.columnCode),
                  // ⚠ `cellText`, не `String(...)`: обидва значення приходять
                  // десятковим рядком сховища, і «12.4000000000» поруч із «12.4»
                  // читалося б як ще одна розбіжність.
                  yours: valueText(item.yourValue),
                  value: valueText(item.theirValue),
                  // ⚠ `null` — «невідомо», і так і написано словом: порожнє
                  // місце читалося б як «ніхто».
                  user: item.theirUser ?? t('grid.conflictUnknownUser'),
                  time: conflictTimeLabel(item.theirChangedAt) ?? t('grid.conflictUnknownTime'),
                })}
          </List.Item>
        ))}
      </List>

      {conflict.more > 0 && (
        // ⛔ Решта не зникає мовчки: людина, яка бачить сто рядків із трьохсот,
        // вважає, що бачить усі.
        <Text size="sm">{t('grid.conflictMore', { count: conflict.more })}</Text>
      )}

      <Group gap="xs" mt="xs">
        <Button size="xs" disabled={!canKeep} loading={busy} onClick={onKeepMine} data-testid="grid-conflict-keep">
          {t('grid.conflictKeepMine')}
        </Button>
        <Button size="xs" variant="default" disabled={busy} onClick={onDiscardMine} data-testid="grid-conflict-discard">
          {t('grid.conflictDiscardMine')}
        </Button>
      </Group>
    </Alert>
  );
}

/**
 * Чи назвав сервер чинну версію рядка — тобто чи є куди писати «Keep mine».
 *
 * ⚠ Перевірка захищена, хоч у типі поле обов'язкове: конфлікт приходить у
 * розширенні `problem+json`, яке компілятор не бачить (`EcrApiError.conflicts`
 * — `unknown[]`), і старіший сервер чи обрізана відповідь без нього мали б
 * вимкнути кнопку, а не знести сітку.
 */
export function hasCurrentVersion(conflict: CellConflictDto): boolean {
  return typeof conflict.currentVersion === 'string' && conflict.currentVersion.length > 0;
}

function valueText(value: unknown): string {
  return value === null || value === undefined ? t('grid.conflictNoValue') : cellText(value);
}
