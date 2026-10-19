import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Підсумок «оновити з дозволу» (`POST /api/v1/documents/{documentId}/permit-bindings/{code}/refresh`). */
export type PermitRefreshResult = components['schemas']['PermitRefreshResultDto'];

/*
 * ⛔ Адреса записана повністю (сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…` разом із
 * методом поруч). Інтерфейсу ще немає — його додає PM-11 (Permits, PM-10).
 */

/**
 * Додає в таблицю документа нові рядки дозволу й оновлює ліміти та довідкові значення, не видаляючи рядків. Рядки, яких
 * у дозволі більше немає, лишаються й повертаються в `removedFromPermit`. Право — як на додавання рядка в цю таблицю;
 * закритий період, поданий чи затверджений аркуш — `403 ECR-ACCS-0403`; невідома прив'язка — `404 ECR-PRM-0404`.
 */
export function refreshPermitBinding(documentId: number, code: string, periodKey: number): Promise<PermitRefreshResult> {
  return apiFetch<PermitRefreshResult>(
    `/api/v1/documents/${String(documentId)}/permit-bindings/${encodeURIComponent(code)}/refresh?periodKey=${String(periodKey)}`,
    { method: 'POST' },
  );
}
