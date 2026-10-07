import { useState, type JSX } from 'react';
import { Popover, UnstyledButton } from '@mantine/core';
import { t } from '@/shared/i18n';
import './gridKeyHint.css';

/** Рядок довідки: клавіші (не перекладаються) і що вони роблять. */
interface ShortcutRow {
  readonly keys: string;
  readonly what: string;
  /** Лише там, де можна редагувати: на аркуші лише для читання рядка немає. */
  readonly edit?: true;
}

/*
 * ⚠ Кожен підпис — окремим викликом із ЛІТЕРАЛОМ (сторож каталогу розбирає лише так).
 * ⚠ Перелік — лише те, що в коді перевірено: стрілки, Tab, Esc — RevoGrid; Enter/F2 — `f2Edit.ts`;
 * Ctrl+C/V — `clipboard.ts`/`bodyPaste.ts`; Ctrl+Z/Y/S — `shortcutKey.ts`; F9 — `isRecalculateKey`;
 * Ctrl+K — палітра оболонки. PageUp/Home/Delete з макета не обіцяємо, доки їх не перевірено.
 */
function rows(): ShortcutRow[] {
  return [
    { keys: '↑ ↓ ← →', what: t('grid.keys.move') },
    { keys: 'Enter · F2', what: t('grid.keys.edit'), edit: true },
    { keys: 'Tab', what: t('grid.keys.tab'), edit: true },
    { keys: 'Esc', what: t('grid.keys.cancel'), edit: true },
    { keys: 'Ctrl+C', what: t('grid.keys.copy') },
    { keys: 'Ctrl+V', what: t('grid.keys.paste'), edit: true },
    { keys: 'Ctrl+Z · Ctrl+Y', what: t('grid.keys.undo'), edit: true },
    { keys: 'Ctrl+S', what: t('grid.keys.save'), edit: true },
    { keys: 'F9', what: t('grid.keys.recalc'), edit: true },
    { keys: 'Ctrl+K', what: t('grid.keys.palette') },
  ];
}

/**
 * Видима підказка клавіш під таблицею і довідка «Keyboard shortcuts» (`UI-41`).
 *
 * Макет — `docs/design/hybrid/screen-document.js`: підказка живе в `aria-label` сітки («Arrow
 * keys move, Enter edits, Ctrl+V pastes from Excel», ~рядок 414) і `kbd: 'F9'` біля
 * «Recalculate» (~246). Задача UI-41 просить зробити її ВИДИМОЮ: оператор не знає, що в сітці є
 * Enter/F2/Ctrl+V. Тому — один дрібний приглушений рядок (KIT §1: спокій за замовчуванням) і
 * подробиці за кліком (`Popover` — «подробиця без зміни екрана», KIT §6.9).
 *
 * ⚠ Аркуш лише для читання — інший текст: обіцяти «Enter edits» там, де редагування закрите,
 * означало б підказку, яка не справджується.
 */
export function GridKeyHint({ readOnly }: { readonly readOnly: boolean }): JSX.Element {
  const [opened, setOpened] = useState(false);
  const list = rows().filter((row) => !readOnly || row.edit !== true);
  const hint = readOnly ? t('grid.keys.hintReadOnly') : t('grid.keys.hint');

  return (
    <div className="ecr-keyhint" data-grid-key-hint={readOnly ? 'read-only' : 'edit'}>
      <span className="ecr-keyhint-text" title={hint}>
        {hint}
      </span>
      <Popover opened={opened} onChange={setOpened} position="top-end" withArrow shadow="md" width={380}>
        <Popover.Target>
          <UnstyledButton
            className="ecr-keyhint-btn"
            aria-expanded={opened}
            aria-haspopup="dialog"
            onClick={() => setOpened((value) => !value)}
            data-grid-key-help=""
          >
            {t('grid.keys.help')}
          </UnstyledButton>
        </Popover.Target>
        <Popover.Dropdown aria-label={t('grid.keys.help')}>
          <dl className="ecr-keyhint-list">
            {list.map((row) => (
              <div key={row.keys} className="ecr-keyhint-row">
                <dt>
                  <kbd>{row.keys}</kbd>
                </dt>
                <dd>{row.what}</dd>
              </div>
            ))}
          </dl>
        </Popover.Dropdown>
      </Popover>
    </div>
  );
}
