import type {
  FormulaResultType,
  MethodologyDraftVersionDto,
  SaveMethodologyFormulaRequest,
} from '@/api/types';

/**
 * Рішення конфігуратора методологій, які **не є розміткою** (`ФВ-9.15`).
 *
 * ⚠ Модуль існує саме тому, що рендер Mantine у jsdom іде хвилинами: те, що
 * можна перевірити лише через сторінку, було б практично неперевіреним. Тут
 * живуть три рішення, кожне з яких мовчки псує дані, якщо помилитися.
 */

/** Формула, яку зараз правлять у діалозі. */
export interface FormulaDraft {
  /** Версія, якій вона належить. */
  readonly versionId: number;

  /** Код — адреса формули; у наявної він не змінюється: на нього посилаються вирази. */
  readonly code: string;

  /** Вираз діалекту методологій. */
  readonly expression: string;

  /** Число чи текст. */
  readonly resultType: FormulaResultType;

  /** Одиниця результату; `null` — безрозмірна або текст. */
  readonly outputUnitId: number | null;

  /** Чи формула нова. */
  readonly isNew: boolean;
}

/**
 * Чи можна правити вміст версії з екрана.
 *
 * ⛔ Дві умови, і жодна не замінює другої. `isEditable` каже СЕРВЕР — це стан
 * версії (`ФВ-13.2`: опублікована незмінна); право каже профіль
 * (`Calculation.EditFormula`). Показати дії правки без першої умови означає
 * вести користувача у відмову `ECR-CALC-0409`; без другої — у `403`.
 *
 * ⚠ Заборону тримає не ця функція, а домен на сервері. Тут — лише відсутність
 * кнопок, які не спрацюють.
 */
export function mayEditContent(
  version: MethodologyDraftVersionDto | undefined,
  hasPermission: boolean,
): boolean {
  return version !== undefined && version.isEditable && hasPermission;
}

/**
 * Версія, яку відкривати за замовчуванням.
 *
 * ⛔ Чернетка, а не перша в переліку. Екран існує заради редагування, і
 * відкривати його на опублікованій версії означало б щоразу починати з глухого
 * кута — з переліку формул, у якому жодної дії немає й бути не може.
 */
export function defaultVersion(
  versions: readonly MethodologyDraftVersionDto[],
  selectedId: string | null,
): MethodologyDraftVersionDto | undefined {
  return (
    versions.find((version) => String(version.id) === selectedId) ??
    versions.find((version) => version.isEditable) ??
    versions[0]
  );
}

/**
 * Тіло запиту на запис формули.
 *
 * ⛔ Текстовий результат іде **без одиниці**. Вимір — властивість числа
 * (`ФВ-16.6`), і сервер таку пару відхиляє (`ECR-CALC-0422`). Лишити одиницю
 * від попереднього вибору означало б показати відмову там, де людина вже все
 * зробила правильно, — вона перемкнула тип, а поле одиниці при цьому зникло з
 * очей.
 */
export function formulaBody(draft: FormulaDraft): SaveMethodologyFormulaRequest {
  return {
    expression: draft.expression,
    resultType: draft.resultType,
    outputUnitId: draft.resultType === 'Text' ? null : draft.outputUnitId,
  };
}
