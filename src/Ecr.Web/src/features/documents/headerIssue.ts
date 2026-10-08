import { useSyncExternalStore } from 'react';
import { EcrApiError } from '@/api/client';
import { problemText } from '@/shared/ui/problemText';

/**
 * Відмова збереження шапки документа (RC14-A), яку бачить і панель шапки
 * (підсвічене поле), і вкладка Issues інспектора.
 *
 * ⚠ Шапка не входить у `ValidationFindingDto` (в неї немає таблиці/рядка), а
 * помилка `ECR-HDR-0422`/`ECR-HDR-4223` приходить лише у відповіді `PATCH …/header`.
 * Тому вона живе тут: панель шапки публікує, інспектор читає. Стан — на документ,
 * у пам'яті вкладки; зникає зі збереженням, правкою зверненого поля, скасуванням
 * чи виходом із документа.
 */
export interface HeaderIssue {
  readonly documentId: number;
  /** Код поля шапки з відмови (`headerFieldCode`); `null` — сервер поля не назвав. */
  readonly fieldCode: string | null;
  /** Назва проблеми з каталогу — показується завжди. */
  readonly title: string;
  /** Локалізоване речення сервера (є лише за `messageKey`). */
  readonly detail: string | null;
  readonly errorCode: string;
}

/** Відмови, що стосуються значення поля шапки (`ErrorCodes.HeaderValueInvalid` / `HeaderEntryNotUsable` / `HeaderFieldNotFound`). */
const HeaderErrorCodes: ReadonlySet<string> = new Set(['ECR-HDR-0422', 'ECR-HDR-4223', 'ECR-HDR-0404']);

/** Зауваження шапки з відмови; `null` — відмова не про значення поля шапки (409, мережа тощо). */
export function headerIssueOf(documentId: number, error: unknown): HeaderIssue | null {
  if (!(error instanceof EcrApiError) || !HeaderErrorCodes.has(error.problem.errorCode)) return null;

  const code = error.problem.extensions2?.['headerFieldCode'];
  const shown = problemText(error);

  return {
    documentId,
    fieldCode: typeof code === 'string' && code.length > 0 ? code : null,
    title: shown.title,
    detail: shown.detail,
    errorCode: error.problem.errorCode,
  };
}

/** Текст під полем: речення сервера, а за його відсутності — назва проблеми. */
export function headerIssueText(issue: HeaderIssue): string {
  return issue.detail ?? issue.title;
}

let current: HeaderIssue | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  listeners.forEach((listener) => listener());
}

export function setHeaderIssue(issue: HeaderIssue | null): void {
  if (current === issue) return;
  current = issue;
  emit();
}

/** Знімає зауваження лише свого документа — чужого не чіпає. */
export function clearHeaderIssue(documentId: number): void {
  if (current?.documentId === documentId) setHeaderIssue(null);
}

/** Зауваження шапки саме цього документа; інакше `null`. */
export function useHeaderIssue(documentId: number): HeaderIssue | null {
  const issue = useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => current,
    () => null,
  );

  return issue?.documentId === documentId ? issue : null;
}

/** Фокус на поле шапки зі зауваження; `false` — поля немає в DOM (панель не змонтована). */
export function focusHeaderField(fieldCode: string): boolean {
  // ⚠ Без селектора з кодом усередині: код поля — довільний текст шаблону.
  const element = [...document.querySelectorAll<HTMLElement>('[data-header-field]')].find(
    (candidate) => candidate.getAttribute('data-header-field') === fieldCode,
  );
  if (element === undefined) return false;

  element.scrollIntoView?.({ block: 'center' });
  element.focus();

  return true;
}
