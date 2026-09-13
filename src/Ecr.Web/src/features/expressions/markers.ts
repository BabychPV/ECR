import type { DiagnosticInfo } from '@/api/types';
import { t } from '@/shared/i18n';

/**
 * Перетворення зауважень сервера на підкреслення в редакторі.
 *
 * ⛔ Сервер віддає позицію ЗМІЩЕННЯМ у символах від початку виразу
 * (`ExpressionDiagnostic.Position`), а Monaco адресує текст рядком і колонкою,
 * і обидва — з ОДИНИЦІ. Три різні системи координат в одному переході: якщо
 * помилитися на одиницю, підкреслення поїде на сусідній символ, і користувач
 * шукатиме помилку не там, де вона є.
 *
 * ⚠ Чиста функція, окремо від Monaco: рендер редактора в jsdom неможливий за
 * прийнятний час, а ламається саме ця арифметика.
 */

/** Підкреслення в редакторі — координати Monaco, всі з одиниці. */
export interface EditorMarker {
  readonly startLineNumber: number;
  readonly startColumn: number;
  readonly endLineNumber: number;
  readonly endColumn: number;
  readonly message: string;
  /** Код помилки — показується поруч і шукається в документації. */
  readonly code: string;
}

/** Позиція в тексті: рядок і колонка, обидва з одиниці. */
interface Position {
  readonly lineNumber: number;
  readonly column: number;
}

/**
 * Будує підкреслення для всіх зауважень.
 *
 * @param text Текст виразу — за ним рахуються рядки й колонки.
 * @param diagnostics Зауваження сервера зі зміщеннями в символах.
 * @returns Підкреслення в координатах Monaco.
 */
export function markersFor(
  text: string,
  diagnostics: readonly DiagnosticInfo[],
): readonly EditorMarker[] {
  return diagnostics.map((diagnostic) => {
    const start = positionAt(text, diagnostic.position);

    // ⚠ Довжина щонайменше один символ. Сервер повідомляє `Length = 1` для
    // всіх зауважень рівня зв'язування (`ReferenceResolver`, `TypeChecker`,
    // `UnitChecker` — усі три жорстко), а нульова довжина дала б підкреслення
    // шириною в нуль пікселів: зауваження є, а на екрані нічого немає.
    const end = positionAt(text, diagnostic.position + Math.max(1, diagnostic.length));

    return {
      startLineNumber: start.lineNumber,
      startColumn: start.column,
      endLineNumber: end.lineNumber,
      endColumn: end.column,
      message: localizedMessage(diagnostic),
      code: diagnostic.code,
    };
  });
}

/**
 * Текст зауваження мовою інтерфейсу (`Q-303`).
 *
 * ⛔ `diagnostic.message` — англійський запасний варіант сервера, а не
 * готовий текст для показу: `Ecr.Expressions` — окрема бібліотека без
 * доступу до каталогу рядків (`IUiStringCatalog`), тож локалізує не сервер,
 * а клієнт за ключем, тим самим `t()`, яким читається решта інтерфейсу.
 *
 * ⚠ `messageKey` є не в кожної діагностики: зауваження рівня зв'язування
 * (`ReferenceResolver`, `TypeChecker`, `UnitChecker`) цю картку свідомо не
 * торкнулася (`docs/build/questions/Q-303.md`) — для них ключа немає, і
 * `diagnostic.message` лишається єдиним текстом, який є.
 */
export function localizedMessage(diagnostic: DiagnosticInfo): string {
  if (diagnostic.messageKey === null || diagnostic.messageKey === undefined) {
    return diagnostic.message;
  }

  return t(diagnostic.messageKey, diagnostic.messageParams ?? undefined);
}

/**
 * Зміщення в символах → рядок і колонка Monaco.
 *
 * ⚠ Зміщення за межами тексту не є помилкою і не обрізається до останнього
 * символа: «неочікуваний кінець виразу» приходить із позицією, що дорівнює
 * ДОВЖИНІ тексту (синтетична лексема `EndOfInput`). Обрізати її означало б
 * підкреслювати останній набраний символ замість порожнього місця за ним —
 * тобто вказувати на правильний текст як на помилку.
 */
export function positionAt(text: string, offset: number): Position {
  const safe = Math.max(0, Math.min(offset, text.length));

  let lineNumber = 1;
  let lineStart = 0;

  for (let i = 0; i < safe; i++) {
    if (text[i] === '\n') {
      lineNumber++;
      lineStart = i + 1;
    }
  }

  return { lineNumber, column: safe - lineStart + 1 };
}
