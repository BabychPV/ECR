import type { ColumnDataSchemaModel, EditorBase, HyperFunc, VNode } from '@revolist/revogrid';
import { t } from '@/shared/i18n';
import { dateOnlyOf } from './cellValue';

/**
 * Редактор комірки `CellDataType.Date` — поле дати з календарем (`R-02`).
 *
 * ⛔ Доти дата редагувалась звичайним текстовим полем: людина мусила знати
 * формат сховища (`2026-09-15`), а будь-який інший запис (`15.09.2026`)
 * сервер відхиляв. Тепер це `<input type="date">`: браузер сам малює поле в
 * локалі людини й відкриває календар, а значення завжди приходить у форматі
 * `yyyy-MM-dd`, який приймає сервер.
 *
 * ⚠ Рідний елемент, а не Mantine `DatePicker`: той живе в React-дереві, а
 * редактор RevoGrid — у дереві веб-компонента, і перенести туди Mantine
 * означало б другий корінь React на кожне відкриття редактора. Рідне поле
 * дає календар, клавіатуру й локаль без жодного кілобайта в чанку сітки.
 *
 * ⚠ Підтвердження — Enter або Tab (як у текстового редактора сітки), а також
 * вибір дня в календарі мишею: `change`, якому не передувало натискання
 * клавіші, — це саме клік у календарі. Друк цифр теж породжує `change` на
 * кожну повну дату, і зберігати на кожну з них не можна — людина ще друкує.
 */
export function createDateCellEditor() {
  return (
    column: ColumnDataSchemaModel,
    save: (value?: unknown, preventFocus?: boolean) => void,
  ): EditorBase => {
    let input: HTMLInputElement | null = null;
    let lastKeyAt = 0;
    let focusTimer: ReturnType<typeof setTimeout> | null = null;

    const commit = (viaTab: boolean): void => {
      if (input === null) return;

      input.blur();
      save(input.value, viaTab);
    };

    const onKeyDown = (event: KeyboardEvent): void => {
      lastKeyAt = Date.now();
      if (event.isComposing) return;

      if (event.key === 'Enter') {
        event.preventDefault();
        event.stopPropagation();
        commit(false);
      } else if (event.key === 'Tab') {
        commit(true);
      }
    };

    const onChange = (): void => {
      if (Date.now() - lastKeyAt > PickerChangeWindowMs) commit(false);
    };

    const editor: EditorBase = {
      render(h: HyperFunc<VNode>): VNode {
        return h('div', { class: 'ecr-date-editor' });
      },

      componentDidRender(): void {
        if (input !== null || !(editor.element instanceof HTMLElement)) return;

        input = editor.element.ownerDocument.createElement('input');
        input.type = 'date';
        input.className = 'ecr-date-editor-input';
        input.setAttribute('aria-label', t('grid.dateEditorLabel'));

        // ⚠ Редагування, почате з цифри, не переноситься в поле: `type="date"`
        // не приймає частковий запис, і `input.value = '2'` дав би порожнє поле
        // так само — лише мовчки. Поле відкривається з поточною датою комірки.
        input.value = dateOnlyOf(column.value) ?? '';

        input.addEventListener('keydown', onKeyDown);
        input.addEventListener('change', onChange);
        editor.element.replaceChildren(input);

        // ⚠ Той самий відкладений фокус, що й у `TextEditor` RevoGrid.
        focusTimer = setTimeout(() => input?.focus(), 0);
      },

      beforeDisconnect(): void {
        input?.blur();
      },

      disconnectedCallback(): void {
        if (focusTimer !== null) clearTimeout(focusTimer);
        input?.removeEventListener('keydown', onKeyDown);
        input?.removeEventListener('change', onChange);
        input = null;
      },
    };

    return editor;
  };
}

/**
 * Скільки після останньої клавіші `change` ще вважається наслідком друку, а не
 * кліку в календарі.
 */
export const PickerChangeWindowMs = 300;
