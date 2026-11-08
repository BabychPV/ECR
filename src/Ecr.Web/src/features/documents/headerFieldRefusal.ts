import { EcrApiError } from '@/api/client';
import { problemText } from '@/shared/ui/problemText';

/**
 * Rule (h1), human 08.11 (Contract tab): a header save refused for ONE named field (`headerFieldCode` in the refusal,
 * e.g. `ECR-HDR-4223 permitNotCoveringMonths` — the permit does not cover the months with data) — the reason to show
 * right under that field.
 *
 * ⚠ Text = the server-localized detail (`problemText`), never the raw developer sentence; a refusal for another field,
 * or one that names no field, gives `null` (the general alert below the form still shows it).
 */
export function headerFieldRefusal(error: unknown, fieldCode: string): string | null {
  if (!(error instanceof EcrApiError)) return null;
  if (error.problem.extensions2?.['headerFieldCode'] !== fieldCode) return null;

  const shown = problemText(error);
  return shown.detail ?? shown.title;
}
