import { hasText, t } from '@/shared/i18n';
import type { ScriptDiagnosticDto } from './types';

/**
 * Діагностики скрипта → підкреслення Monaco. Чиста функція (без Monaco).
 *
 * ⚠ Рядок і колонка від сервера вже 1-базові, як у Monaco, — перерахунку
 * зміщень (на відміну від `expressions/markers.ts`) тут немає.
 */

export interface ScriptMarker {
  readonly startLineNumber: number;
  readonly startColumn: number;
  readonly endLineNumber: number;
  readonly endColumn: number;
  readonly message: string;
  readonly code: string;
}

/** Текст мовою інтерфейсу за `messageKey`; без ключа в каталозі — запасний `message`. */
export function localizedScriptMessage(diagnostic: ScriptDiagnosticDto): string {
  if (
    diagnostic.messageKey === null ||
    diagnostic.messageKey === '' ||
    !hasText(diagnostic.messageKey)
  ) {
    return diagnostic.message;
  }
  return t(diagnostic.messageKey, diagnostic.params ?? undefined);
}

/** Підкреслення для всіх діагностик; довжина — щонайменше один символ. */
export function scriptMarkers(diagnostics: readonly ScriptDiagnosticDto[]): ScriptMarker[] {
  return diagnostics.map((diagnostic) => {
    const line = Math.max(1, diagnostic.line);
    const column = Math.max(1, diagnostic.column);
    return {
      startLineNumber: line,
      startColumn: column,
      endLineNumber: line,
      endColumn: column + Math.max(1, diagnostic.length),
      message: localizedScriptMessage(diagnostic),
      code: diagnostic.code,
    };
  });
}
