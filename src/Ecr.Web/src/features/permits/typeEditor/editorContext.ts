import { createContext, useContext } from 'react';
import type { PermitType } from '../api/types';
import type { TypeDraft } from './draftModel';

/*
 * Стан редактора для діалогів PM-08c (Permits, PM-08b). Діалог бере робочу копію, змінює її
 * чистими функціями `draftModel` через `apply` і, якщо дія іде на сервер (публікація, нова версія,
 * видалення), спершу `flush()` — зберігає чернетку, а після відповіді передає її в `acceptServer`.
 */

/** Стан збереження чернетки. */
export type SaveState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'dirty'; readonly count: number }
  | { readonly kind: 'saving' }
  | { readonly kind: 'saved'; readonly at: Date }
  | { readonly kind: 'failed'; readonly count: number }
  | { readonly kind: 'conflict' };

export interface TypeEditorApi {
  /** Код типу з адреси. */
  readonly code: string;
  /** Останнє, що відомо про тип на сервері. */
  readonly type: PermitType;
  /** Робоча копія (те, що показує екран). */
  readonly draft: TypeDraft;
  /** Чи можна правити: лише чернетка (`hasUnpublishedChanges`). */
  readonly editable: boolean;
  readonly save: SaveState;
  /** Застосовує зміну до робочої копії й планує збереження. Для неправного типу — нічого не робить. */
  apply(change: (draft: TypeDraft) => TypeDraft): void;
  /** Зберігає зараз і чекає. `true` — на сервері все, що на екрані. */
  flush(): Promise<boolean>;
  /** Передає відповідь сервера (після публікації чи нової версії) як нову збережену основу. */
  acceptServer(type: PermitType): void;
  /** Перечитує тип із сервера, відкидаючи незбережене (після конфлікту). */
  reload(): Promise<void>;
}

export const TypeEditorContext = createContext<TypeEditorApi | null>(null);

/** Стан редактора; поза сторінкою редактора — помилка програміста. */
export function useTypeEditorApi(): TypeEditorApi {
  const api = useContext(TypeEditorContext);
  if (api === null) throw new Error('useTypeEditorApi: потрібен TypeEditorContext.Provider (сторінка редактора типу).');
  return api;
}
