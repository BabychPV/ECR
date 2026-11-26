import { type JSX } from 'react';
import { ActionIcon, Anchor, Button, Group, List, Popover, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { showDone } from '@/shared/ui/notify';
import { requestCellNavigation } from './cellNavigation';
import { needsInputHint, type NeedsInputCell } from './needsInput';
import { stepNeedsInput, useNeedsInputSummary } from './needsInputStore';

/** Скільки пунктів переліку в спливаючому вікні (решта — «…», до них веде F8). */
const ListLimit = 50;

interface NeedsInputCounterProps {
  readonly cells: readonly NeedsInputCell[];
  readonly tableDefId: number | undefined;
  readonly tableInstanceId: number;
  readonly periodKey: number;
  /** Назва колонки за кодом (заголовок зрізу мовою інтерфейсу). */
  readonly columnHeader: (code: string) => string;
  /** Підпис рядка (назва рядка шаблону або його номер). */
  readonly rowLabel: (rowKey: string) => string;
}

/**
 * ✎ DOCUX L2 (§4.4 плану): тулбар таблиці — «N required cells empty ‹ ›». Лічить КОМІРКИ (не колонки, як колишня кнопка
 * «N required column(s) not filled yet»); червона, коли хоч одна блокує подання, інакше жовта; ‹ › і F8 / Shift+F8 —
 * до попередньої / наступної по всьому аркушу; перелік у спливаючому вікні — посилання, що ведуть до комірки.
 *
 * ⚠ Колір — разом зі словом і кількістю (ніколи сам): тон Mantine `statusError`/`statusWarning` пройшов перевірку
 * контрасту теми (`theme.ts`).
 */
export function NeedsInputCounter({
  cells,
  tableDefId,
  tableInstanceId,
  periodKey,
  columnHeader,
  rowLabel,
}: NeedsInputCounterProps): JSX.Element | null {
  const { unmountedTables } = useNeedsInputSummary();
  if (cells.length === 0) return null;

  const blocking = cells.some((cell) => cell.level === 'block');
  const here = { tableInstanceId, periodKey };
  const step = (direction: 1 | -1): void => {
    if (!stepNeedsInput(direction, here)) showDone(t('grid.needsInput.none'));
  };

  return (
    <Group gap="xs" wrap="nowrap" data-needs-input-counter={blocking ? 'block' : 'warn'}>
      <Popover position="bottom-start" withArrow shadow="md" width={480} trapFocus={false}>
        <Popover.Target>
          <Button variant="light" color={blocking ? 'statusError' : 'statusWarning'} data-needs-input-count={cells.length}>
            {t('grid.needsInput.counter', { count: cells.length })}
          </Button>
        </Popover.Target>
        <Popover.Dropdown aria-label={t('grid.needsInput.listLabel')}>
          <List size="sm" data-needs-input-list="">
            {cells.slice(0, ListLimit).map((cell) => (
              <List.Item key={`${cell.rowKey}:${cell.columnCode}`}>
                <Anchor
                  component="button"
                  type="button"
                  size="sm"
                  data-needs-input-link={`${cell.rowKey}:${cell.columnCode}`}
                  onClick={() => {
                    if (tableDefId === undefined) return;
                    requestCellNavigation({ tableDefId, tableInstanceId, rowKey: cell.rowKey, columnCode: cell.columnCode });
                  }}
                >
                  {`${columnHeader(cell.columnCode)} · ${rowLabel(cell.rowKey)}`}
                </Anchor>
                <Text span size="sm" c="dimmed">{` — ${needsInputHint(cell)}`}</Text>
              </List.Item>
            ))}
          </List>
          {cells.length > ListLimit && <Text size="sm">…</Text>}
          {/* ✎ DOCUX L9: таблиці, ще не відкриті прокруткою, — названо прямо, а не мовчки «усе». */}
          {unmountedTables > 0 && (
            <Text size="xs" c="dimmed" mt="xs" data-needs-input-unmounted={unmountedTables}>
              {t('grid.needsInput.otherTables', { count: unmountedTables })}
            </Text>
          )}
        </Popover.Dropdown>
      </Popover>
      <ActionIcon
        variant="default"
        aria-label={t('grid.needsInput.prev')}
        title={t('grid.needsInput.prev')}
        aria-keyshortcuts="Shift+F8"
        onClick={() => step(-1)}
        data-needs-input-prev=""
      >
        ‹
      </ActionIcon>
      <ActionIcon
        variant="default"
        aria-label={t('grid.needsInput.next')}
        title={t('grid.needsInput.next')}
        aria-keyshortcuts="F8"
        onClick={() => step(1)}
        data-needs-input-next=""
      >
        ›
      </ActionIcon>
    </Group>
  );
}
