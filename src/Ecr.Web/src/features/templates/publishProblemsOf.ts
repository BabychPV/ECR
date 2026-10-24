import { EcrApiError } from '@/api/client';
import type { DiagnosticInfo } from '@/api/types';

/**
 * Перелік проблем, через які сервер відмовив у публікації версії шаблону.
 *
 * ⛔ A2-01: відмова публікації несе ПЕРЕЛІК у `details.diagnostics` (на проводі —
 * плоским полем `diagnostics`), але тост показував лише головне речення:
 * «… the first is ECR-TMPL-4224 at position 0». Автор шаблону не бачив ні
 * правил, ні таблиці, ні жодної з решти проблем — а публікація відмовляє з УСІМА
 * одразу, щоб не виправляти їх по одній.
 */
export function publishProblemsOf(error: unknown): DiagnosticInfo[] {
  if (!(error instanceof EcrApiError)) return [];

  const list = error.problem.extensions2?.['diagnostics'];

  if (!Array.isArray(list)) return [];

  return list.filter(
    (item): item is DiagnosticInfo =>
      typeof item === 'object' && item !== null && typeof (item as { code?: unknown }).code === 'string',
  );
}
