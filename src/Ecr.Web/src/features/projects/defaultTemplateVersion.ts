/**
 * Яку версію шаблону форма створення проєкту пропонує за замовчуванням (ФВ-1.2, рішення людини 12.10).
 *
 * ⚠ Це лише ПРОПОЗИЦІЯ для НОВОГО проєкту. Версія наявного проєкту не змінюється публікацією нової: її
 * фіксує створення, а переносить лише `POST /documents/{id}/migrate-version` (`Template.Edit`).
 */

/** Опублікована версія шаблону в переліку форми. */
export interface PublishedVersionOption {
  /** `String(version.id)` — значення `Select`. */
  value: string;
  label: string;
  /** Момент публікації (ISO); `null` — сервер не віддав. */
  publishedAt: string | null;
  /** Шаблон архівований: для нових проєктів не пропонується (але вручну обрати його можна). */
  archived: boolean;
}

/**
 * Остання ОПУБЛІКОВАНА версія серед варіантів: найпізніша за `publishedAt`, за рівності — з більшим `id`.
 *
 * ⚠ Не «найбільший номер»: `version` — довільний рядок («1.0.0.0», «v2», «2026-Q4»), числом не впорядковується, а
 * публікація v1.1 після v3 — законна. Хронологія публікації однозначна.
 * ⚠ Кілька шаблонів: пропонується остання опублікована ВЗАГАЛІ (зазвичай та, яку щойно випустили). Архівні
 * шаблони не пропонуються. Немає жодного кандидата — `null` (поле лишається порожнім, обирає людина).
 */
export function pickDefaultVersionId(options: readonly PublishedVersionOption[]): string | null {
  let best: { value: string; at: number; id: number } | null = null;

  for (const option of options) {
    if (option.archived) continue;

    const parsed = option.publishedAt === null ? Number.NaN : Date.parse(option.publishedAt);
    const at = Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed;
    const id = Number(option.value);

    if (best === null || at > best.at || (at === best.at && id > best.id)) {
      best = { value: option.value, at, id };
    }
  }

  return best?.value ?? null;
}
