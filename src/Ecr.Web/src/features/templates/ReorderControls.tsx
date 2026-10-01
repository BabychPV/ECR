import { useEffect, useRef, useState, type CSSProperties, type DragEvent, type JSX, type ReactNode } from 'react';
import { ActionIcon, Box, Group } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Перетягування рядків списку (`ФВ-2.6`) і його доступна альтернатива.
 *
 * ⛔ Перетягування — НЕ єдиний шлях. WCAG 2.5.7 вимагає для кожної дії
 * перетягуванням альтернативи одним вказівником, а 2.1.1 — з клавіатури.
 * Обидві дає пара кнопок «вище/нижче»: звичайні кнопки, зупинка Tab,
 * `Enter`/`Space`, доступна назва з іменем елемента. Ручка перетягування
 * тому прихована від читача (`aria-hidden`) — вона дублює кнопки для миші.
 *
 * ⚠ Рідний HTML5 drag-and-drop без бібліотек: нових пакетів у лінії не
 * ставимо (`CLAUDE.md`, контракт сабагента), а переставляти треба лише
 * рядки однієї таблиці.
 */

interface DragState {
  readonly from: number | null;
  readonly over: number | null;
}

const Idle: DragState = { from: null, over: null };

/** Підсвітка місця, куди впаде елемент. */
const DropTargetStyle: CSSProperties = {
  outline: '2px dashed var(--mantine-primary-color-filled)',
  outlineOffset: -2,
};

export interface DragReorder {
  /** Властивості рядка-цілі (`<tr>`). */
  readonly targetProps: (index: number) => {
    onDragOver?: (event: DragEvent<HTMLElement>) => void;
    onDrop?: (event: DragEvent<HTMLElement>) => void;
    style?: CSSProperties;
    'data-drop-target'?: 'true';
  };
  /** Властивості ручки, за яку тягнуть. */
  readonly handleProps: (index: number) => {
    draggable: boolean;
    onDragStart?: (event: DragEvent<HTMLElement>) => void;
    onDragEnd?: () => void;
  };
}

/**
 * Стан перетягування для одного списку.
 *
 * @param onMove Викликається з позиціями «звідки» й «куди» після скидання.
 * @param enabled `false` — нічого не перетягується (немає права, запис іде).
 */
export function useDragReorder(onMove: (from: number, to: number) => void, enabled: boolean): DragReorder {
  const [state, setState] = useState<DragState>(Idle);

  return {
    targetProps: (index) => {
      if (!enabled) return {};

      return {
        onDragOver: (event) => {
          // ⚠ Лише своє перетягування: текст чи файл, занесені ззовні, ціллю не є.
          if (state.from === null) return;
          event.preventDefault();
          if (event.dataTransfer !== null) event.dataTransfer.dropEffect = 'move';
          if (state.over !== index) setState({ from: state.from, over: index });
        },
        onDrop: (event) => {
          if (state.from === null) return;
          event.preventDefault();
          const from = state.from;
          setState(Idle);
          if (from !== index) onMove(from, index);
        },
        ...(state.from !== null && state.over === index && state.from !== index
          ? { style: DropTargetStyle, 'data-drop-target': 'true' as const }
          : {}),
      };
    },
    handleProps: (index) => {
      if (!enabled) return { draggable: false };

      return {
        draggable: true,
        onDragStart: (event) => {
          if (event.dataTransfer !== null) {
            event.dataTransfer.effectAllowed = 'move';
            // ⚠ Firefox не починає перетягування без даних у `dataTransfer`.
            event.dataTransfer.setData('text/plain', String(index));
          }
          setState({ from: index, over: null });
        },
        onDragEnd: () => setState(Idle),
      };
    },
  };
}

interface ReorderCellProps {
  readonly index: number;
  readonly count: number;
  /** Ім'я елемента для доступної назви кнопок. */
  readonly name: string;
  readonly disabled: boolean;
  readonly onMove: (from: number, to: number) => void;
  readonly drag: DragReorder;
  /** Пояснення, чому кнопки вимкнені (`aria-describedby`). */
  readonly describedBy?: string;
}

/** Ручка перетягування й кнопки «вище/нижче» для одного рядка списку. */
export function ReorderCell({
  index,
  count,
  name,
  disabled,
  onMove,
  drag,
  describedBy,
}: ReorderCellProps): JSX.Element {
  const handle = drag.handleProps(index);

  // ⛔ Фокус іде за елементом, а не лишається на місці (WCAG 2.4.3). Під час
  // запису кнопки `disabled` — браузер знімає з них фокус; після перестановки
  // React переносить рядок у DOM — і фокус падає на `<body>`. Без цього
  // клавіатурна людина після кожного «↓» шукала б рядок заново з початку
  // сторінки. Тому запам'ятовуємо натиснутий напрямок і, щойно кнопки знову
  // доступні, ставимо фокус на ту саму кнопку в новій позиції; на межі
  // списку (вона там вимкнена) — на сусідню.
  const up = useRef<HTMLButtonElement>(null);
  const down = useRef<HTMLButtonElement>(null);
  const pressed = useRef<'up' | 'down' | null>(null);

  useEffect(() => {
    if (pressed.current === null || disabled) return;
    const wanted = pressed.current === 'up' ? up.current : down.current;
    const other = pressed.current === 'up' ? down.current : up.current;
    pressed.current = null;
    const target = wanted !== null && !wanted.disabled ? wanted : other;
    if (target !== null && !target.disabled) target.focus();
  }, [index, disabled]);

  const move = (direction: 'up' | 'down'): void => {
    pressed.current = direction;
    onMove(index, direction === 'up' ? index - 1 : index + 1);
  };

  return (
    <Group gap="xs" wrap="nowrap">
      <Box
        component="span"
        aria-hidden="true"
        data-reorder-handle={index}
        title={disabled ? undefined : t('reorder.handle')}
        px="xs"
        style={{ cursor: handle.draggable ? 'grab' : 'default', userSelect: 'none' }}
        {...handle}
      >
        ⠿
      </Box>
      <ActionIcon
        ref={up}
        size="sm"
        variant="subtle"
        aria-label={t('reorder.moveUp', { name })}
        aria-describedby={describedBy}
        disabled={disabled || index === 0}
        onClick={() => move('up')}
      >
        ↑
      </ActionIcon>
      <ActionIcon
        ref={down}
        size="sm"
        variant="subtle"
        aria-label={t('reorder.moveDown', { name })}
        aria-describedby={describedBy}
        disabled={disabled || index === count - 1}
        onClick={() => move('down')}
      >
        ↓
      </ActionIcon>
    </Group>
  );
}

/**
 * Список, рядки якого можна переставляти: тримає стан перетягування одного
 * списку й віддає його кожному рядку (хук не можна кликати в циклі рендеру
 * сторінки, тож стан живе тут).
 */
export function ReorderableRows<T>({
  items,
  enabled,
  onMove,
  children,
}: {
  readonly items: readonly T[];
  readonly enabled: boolean;
  readonly onMove: (from: number, to: number) => void;
  readonly children: (item: T, index: number, drag: DragReorder) => ReactNode;
}): JSX.Element {
  const drag = useDragReorder(onMove, enabled);

  return <>{items.map((item, index) => children(item, index, drag))}</>;
}
