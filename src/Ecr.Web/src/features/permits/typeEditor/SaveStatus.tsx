import type { JSX } from 'react';
import { Anchor, Text } from '@mantine/core';
import { language, t } from '@/shared/i18n';
import type { SaveState } from './editorContext';

/** Час «ГГ:ХХ» мовою користувача. */
export function clockTime(at: Date): string {
  return at.toLocaleTimeString(language(), { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
}

export interface SaveStatusProps {
  readonly save: SaveState;
  /** Остання зміна на сервері (для стану «спокій»). */
  readonly updatedAt: string | null;
  /** Скільки вад не дають зберегти (див. `validateDraft`). */
  readonly problemCount: number;
  readonly onRetry: () => void;
  readonly onReload: () => void;
}

/**
 * Рядок стану збереження у шапці (мокап: «N unpublished changes» / «Saved HH:MM»): `role="status"`,
 * тож зміни озвучуються ввічливо. Стани — з `useTypeEditor`: правка чекає, іде запит, збережено,
 * не вдалось (з «Retry»), конфлікт (з «Reload»).
 */
export function SaveStatus({ save, updatedAt, problemCount, onRetry, onReload }: SaveStatusProps): JSX.Element {
  let text: string;
  let action: JSX.Element | null = null;

  switch (save.kind) {
    case 'saving':
      text = t('permits.editor.save.saving');
      break;
    case 'dirty':
      text =
        problemCount > 0
          ? t('permits.editor.save.blocked', { count: save.count, problems: problemCount })
          : t('permits.editor.save.unsaved', { count: save.count });
      break;
    case 'failed':
      text = t('permits.editor.save.unsaved', { count: save.count });
      action = (
        <Anchor component="button" type="button" size="sm" onClick={onRetry}>
          {t('permits.editor.save.retry')}
        </Anchor>
      );
      break;
    case 'conflict':
      text = t('permits.editor.save.conflict');
      action = (
        <Anchor component="button" type="button" size="sm" onClick={onReload}>
          {t('permits.editor.save.reload')}
        </Anchor>
      );
      break;
    case 'saved':
      text = t('permits.editor.save.saved', { time: clockTime(save.at) });
      break;
    default:
      text = updatedAt === null ? t('permits.editor.save.savedNoTime') : t('permits.editor.save.saved', { time: clockTime(new Date(updatedAt)) });
  }

  return (
    <Text size="sm" c="dimmed" role="status" component="span">
      {text}
      {action !== null && <> · {action}</>}
    </Text>
  );
}
