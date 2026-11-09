import { useId, useState, type JSX, type ReactNode } from 'react';
import { Box, Group, Text, UnstyledButton } from '@mantine/core';
import { t } from '@/shared/i18n';
import { useSession } from '@/shared/session/useSession';
import './collapsibleNote.css';

/** Ключ пам'яті згортання: окремо на людину й на пояснення. */
export function noteStorageKey(noteId: string, userId: number | undefined): string {
  return `ecr.note.${noteId}.${userId === undefined ? 'anon' : String(userId)}`;
}

/** Згорнуто? `null` — людина ще не обирала (або сховище недоступне: приватне вікно, заборона сайту). */
export function readNoteCollapsed(key: string): boolean | null {
  try {
    const value = window.localStorage.getItem(key);

    return value === null ? null : value === '1';
  } catch {
    return null;
  }
}

function writeNoteCollapsed(key: string, collapsed: boolean): void {
  try {
    window.localStorage.setItem(key, collapsed ? '1' : '0');
  } catch {
    // Сховище недоступне — вибір діє до кінця сесії сторінки, і це не помилка для людини.
  }
}

/**
 * ✎ fg6 (людина 08.11: «ця частина не має займати все вікно і має мати можливість згортатися»): пояснення або
 * інформаційна панель — компактна (≤ ~2 рядки на 1335 px), згортається, згорнута показує ОДИН рядок підсумку.
 * Вибір пам'ятається на людину (`localStorage`, з `try/catch` — сторінка працює й без сховища).
 *
 * ⚠ Не `shared/ui`: там зараз працює інша сесія (сітка). Перенести, коли сусідні екрани захочуть те саме.
 */
export function CollapsibleNote({
  noteId,
  summary,
  children,
  defaultCollapsed = false,
}: {
  /** Стабільний ідентифікатор пояснення (частина ключа пам'яті). */
  readonly noteId: string;
  /** Один рядок, що видно і згорнутим. */
  readonly summary: string;
  /** Подробиці — до ~2 рядків. */
  readonly children: ReactNode;
  readonly defaultCollapsed?: boolean;
}): JSX.Element {
  const userId = useSession().data?.userId;
  const key = noteStorageKey(noteId, userId);
  const [chosen, setChosen] = useState<{ key: string; collapsed: boolean } | null>(null);
  const collapsed = chosen?.key === key ? chosen.collapsed : (readNoteCollapsed(key) ?? defaultCollapsed);
  const bodyId = useId();

  const toggle = (): void => {
    writeNoteCollapsed(key, !collapsed);
    setChosen({ key, collapsed: !collapsed });
  };

  return (
    <Box className="ecr-note" data-note={noteId} data-collapsed={collapsed ? 'true' : 'false'} mb="xs">
      <Group gap="xs" wrap="nowrap" align="flex-start">
        <UnstyledButton
          className="ecr-note-toggle"
          aria-expanded={!collapsed}
          aria-controls={bodyId}
          onClick={toggle}
          title={collapsed ? t('common.noteExpand') : t('common.noteCollapse')}
        >
          <span aria-hidden="true" className="ecr-note-chevron">
            {collapsed ? '▸' : '▾'}
          </span>
          <Text span size="sm" fw={500} className="ecr-note-summary">
            {summary}
          </Text>
        </UnstyledButton>
      </Group>
      <Text id={bodyId} size="xs" c="dimmed" hidden={collapsed} className="ecr-note-body">
        {children}
      </Text>
    </Box>
  );
}
