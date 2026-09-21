import { apiFetch } from '@/api/client';
import type { RenameTemplateRequest, TemplateCard } from '@/api/types';

/**
 * Картка шаблону: читання, перейменування, архівування (директива №15,
 * `BE-26`).
 *
 * ⚠ Коду шаблону тут немає жодною дією: це бізнес-ключ, за яким на шаблон
 * посилаються проєкти, і змінити його не можна (`templates.codeHint`).
 *
 * ⛔ Лічильник залежних приходить ТІЄЮ САМОЮ відповіддю, що й картка
 * (`TemplateCard.dependents`), і окремого запиту за ним немає навмисно: екран
 * архівування не має показувати кнопку раніше, ніж число, заради якого її
 * натискають.
 */

/** Картка шаблону разом із лічильником залежних. */
export function fetchTemplateCard(templateId: number): Promise<TemplateCard> {
  return apiFetch<TemplateCard>(`/api/v1/templates/${String(templateId)}`);
}

/** Змінює назву шаблону; повертає оновлену картку. */
export function renameTemplate(
  templateId: number,
  nameL10n: Record<string, string>,
): Promise<TemplateCard> {
  return apiFetch<TemplateCard>(`/api/v1/templates/${String(templateId)}`, {
    method: 'PUT',
    body: JSON.stringify({ nameL10n } satisfies RenameTemplateRequest),
  });
}

/**
 * Архівує шаблон: для нових документів він більше не пропонується.
 *
 * ⚠ Наявні документи працюють далі — архів не зупиняє заповнення вже заведених
 * форм. Повторне архівування сервер відхиляє з `409 ECR-TMPL-0409`.
 */
export function archiveTemplate(templateId: number): Promise<TemplateCard> {
  return apiFetch<TemplateCard>(`/api/v1/templates/${String(templateId)}/archive`, {
    method: 'POST',
  });
}

/** Повертає архівований шаблон в обіг. */
export function restoreTemplate(templateId: number): Promise<TemplateCard> {
  return apiFetch<TemplateCard>(`/api/v1/templates/${String(templateId)}/restore`, {
    method: 'POST',
  });
}

/**
 * Скільки всього роботи зачепить архівування — одне число для попередження.
 *
 * ⚠ Версії в суму НЕ входять: вони належать самому шаблону, а питання перед
 * архівуванням — «скільки чужого на нього спирається».
 */
export function dependentWorkCount(card: TemplateCard): number {
  return card.dependents.projects + card.dependents.documents;
}
