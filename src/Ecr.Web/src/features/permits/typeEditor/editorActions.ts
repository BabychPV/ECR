import { createContext, useContext, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';

/*
 * Точка підключення діалогів конструктора типу (Permits, PM-08b → PM-08c).
 *
 * Кнопки редактора (PM-08b) НЕ відкривають діалогів самі: вони кличуть `EditorActions`. PM-08c
 * реалізує діалоги й читає `?dialog=<id>` (`readDialog`); типова реалізація нижче лише ставить
 * параметр адреси, тож без PM-08c кнопки чесно нічого не показують, а діалог відкривається
 * посиланням і працює з кнопкою «Назад». Ідентифікатори — ті самі, що в макеті
 * (`ctx.openDialog('…')` у `screens-permits.js`, розділ 4).
 */

/** Параметри адреси сторінки редактора. */
export const EditorParams = {
  /** Вкладка: `structure` (типово) | `rules` | `versions`. */
  tab: 'tab',
  /** Вибрана таблиця дерева; немає — перша, `__head` — «Permit details». */
  table: 't',
  /** Код стовпця, відкритого в інспекторі. */
  panel: 'panel',
  /** Відкритий діалог (див. `EditorDialogId`). */
  dialog: 'dialog',
  /** Таблиця, до якої відноситься діалог `delete-table`. */
  target: 'table',
} as const;

/** Вузол «Permit details» у дереві (значення `?t=`). */
export const HeadNode = '__head';

/** Ідентифікатори діалогів. */
export const EditorDialogIds = [
  'add-table',
  'add-column',
  'add-series',
  'delete-table',
  'add-rule',
  'publish-type',
  'new-version',
  'delete-type',
] as const;
export type EditorDialogId = (typeof EditorDialogIds)[number];

/** Дії редактора, які відкривають діалоги. */
export interface EditorActions {
  openAddTable(): void;
  openAddColumn(): void;
  openAddSeries(): void;
  openDeleteTable(tableCode: string): void;
  openAddRule(): void;
  openPublish(): void;
  openNewVersion(): void;
  openDeleteType(): void;
}

/** Відкритий діалог з адреси: невідомий ідентифікатор — `null`. */
export function readDialog(params: URLSearchParams): { id: EditorDialogId; tableCode: string | null } | null {
  const id = params.get(EditorParams.dialog);
  if (id === null || !(EditorDialogIds as readonly string[]).includes(id)) return null;
  return { id: id as EditorDialogId, tableCode: params.get(EditorParams.target) };
}

/** Нова адреса після відкриття діалогу: решта параметрів (вкладка, таблиця, інспектор) лишається. */
export function withDialog(params: URLSearchParams, id: EditorDialogId, tableCode?: string): URLSearchParams {
  const next = new URLSearchParams(params);
  next.set(EditorParams.dialog, id);
  if (tableCode === undefined) next.delete(EditorParams.target);
  else next.set(EditorParams.target, tableCode);
  return next;
}

/** Нова адреса після закриття діалогу. */
export function withoutDialog(params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  next.delete(EditorParams.dialog);
  next.delete(EditorParams.target);
  return next;
}

/** Провайдер PM-08c (або тест) підміняє типову реалізацію. */
export const TypeEditorActionsContext = createContext<EditorActions | null>(null);

/** Типова реалізація: ставить `?dialog=<id>` у адресу (запис в історію — «Назад» закриває діалог). */
export function useUrlEditorActions(): EditorActions {
  const [params, setParams] = useSearchParams();

  return useMemo(() => {
    const open = (id: EditorDialogId, tableCode?: string): void => setParams(withDialog(params, id, tableCode));
    return {
      openAddTable: () => open('add-table'),
      openAddColumn: () => open('add-column'),
      openAddSeries: () => open('add-series'),
      openDeleteTable: (tableCode) => open('delete-table', tableCode),
      openAddRule: () => open('add-rule'),
      openPublish: () => open('publish-type'),
      openNewVersion: () => open('new-version'),
      openDeleteType: () => open('delete-type'),
    };
  }, [params, setParams]);
}

/** Дії для кнопок: з контексту, якщо PM-08c його надав, інакше типові. */
export function useEditorActions(): EditorActions {
  const provided = useContext(TypeEditorActionsContext);
  const fallback = useUrlEditorActions();
  return provided ?? fallback;
}
