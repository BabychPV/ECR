import { EcrApiError } from '@/api/client';
import type { ScriptDiagnosticDto } from './types';

/**
 * Стан тексту редактора скрипта: що в полі, що лежить на сервері, який хеш
 * слати як `expectedHash`. Чистий редуктор без React і мережі.
 */

export interface ScriptEditorState {
  /** Текст у редакторі. */
  readonly text: string;
  /** Текст, який, як відомо, лежить на сервері. */
  readonly savedText: string;
  /** Хеш серверного тексту — `expectedHash` наступного збереження. */
  readonly hash: string;
  /** Сервер відмовив у збереженні: текст змінено деінде (`scriptChanged`). */
  readonly conflict: boolean;
}

export type ScriptEditorAction =
  /** Завантажено з сервера (перше читання або «Перезавантажити»). */
  | { readonly type: 'loaded'; readonly text: string; readonly hash: string }
  /** Людина змінила текст. */
  | { readonly type: 'edited'; readonly text: string }
  /** Збережено текст `submitted`; поле могло піти вперед, поки запит летів. */
  | { readonly type: 'saved'; readonly submitted: string; readonly hash: string }
  /** `409 scriptChanged`. */
  | { readonly type: 'conflict' };

export const EmptyScriptEditorState: ScriptEditorState = {
  text: '',
  savedText: '',
  hash: '',
  conflict: false,
};

export function scriptEditorReducer(
  state: ScriptEditorState,
  action: ScriptEditorAction,
): ScriptEditorState {
  switch (action.type) {
    case 'loaded':
      return { text: action.text, savedText: action.text, hash: action.hash, conflict: false };
    case 'edited':
      return action.text === state.text ? state : { ...state, text: action.text };
    case 'saved':
      return { ...state, savedText: action.submitted, hash: action.hash, conflict: false };
    case 'conflict':
      return { ...state, conflict: true };
  }
}

/** Чи є незбережені правки. */
export function isScriptDirty(state: ScriptEditorState): boolean {
  return state.text !== state.savedText;
}

/**
 * Чи доступне «Зберегти»: є що зберігати, версію можна правити і тіло не
 * порожнє (порожній текст сервер відхиляє `422 scriptEmpty`).
 */
export function canSaveScript(state: ScriptEditorState, readOnly: boolean): boolean {
  return !readOnly && !state.conflict && isScriptDirty(state) && state.text.trim() !== '';
}

/** Тіло `PUT …/script`: порожній хеш (тексту ще не було) не шлемо. */
export function saveScriptBody(state: ScriptEditorState): {
  readonly text: string;
  readonly expectedHash?: string;
} {
  return state.hash === ''
    ? { text: state.text }
    : { text: state.text, expectedHash: state.hash };
}

export const ScriptChangedMessageKey = 'err.ECR-CALC-0409.scriptChanged';

/** Чи це `409 ECR-CALC-0409.scriptChanged` (хеш не збігся). */
export function isScriptChangedError(error: unknown): boolean {
  if (!(error instanceof EcrApiError)) return false;
  if (error.problem.errorCode !== 'ECR-CALC-0409') return false;

  return error.problem.extensions2?.['messageKey'] === ScriptChangedMessageKey;
}

const NoDiagnostics: readonly ScriptDiagnosticDto[] = [];

/**
 * Діагностики для підкреслення й переліку проблем. ⛔ Діагностики збереженого тексту (рядки й колонки) показуються
 * лише поки текст у редакторі ним і є: інакше підкреслення лягало б на чужі рядки (перевірка ще не відповіла, впала
 * чи текст порожній — `validate` для порожнього не викликається, і попередня відповідь не повинна «прилипнути»).
 */
export function liveDiagnostics(args: {
  readonly text: string;
  readonly savedText: string;
  readonly validated: readonly ScriptDiagnosticDto[] | undefined;
  readonly saved: readonly ScriptDiagnosticDto[] | undefined;
}): readonly ScriptDiagnosticDto[] {
  if (args.text.trim() === '') return NoDiagnostics;
  if (args.validated !== undefined) return args.validated;
  return args.text === args.savedText ? (args.saved ?? NoDiagnostics) : NoDiagnostics;
}

/** Замінює `[start, end)` тексту: застосування «Редагувати як таблицю». */
export function replaceTextRange(text: string, start: number, end: number, replacement: string): string {
  return text.slice(0, start) + replacement + text.slice(end);
}
