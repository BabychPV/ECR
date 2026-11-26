import { allSheetsApproved } from '@/features/documents/documentLock';

/**
 * ✎ DOCUX L1 / Q3 координатора: ЄДИНЕ місце рішення «чи можна перерахувати цей аркуш × період» на клієнті.
 *
 * Клієнт іде за правдою сервера. Контракт стану розрахунку (lane doc-ux-server, L3) віддає для аркуша × періоду власне
 * «можна перерахувати» — щойно поле є (`serverCanRecalculate !== undefined/null`), рішення бере ЙОГО, і правило нижче
 * не діє. Доти — правило сервера, відтворене тут (`RecalculateDocumentHandler`, ST-3 підтверджено 26.11): поданий аркуш
 * перераховується; «нічого рахувати» — лише коли затверджено оригінал і кожну копію; річний документ не відмовляє.
 */
export type RecalcBlock = 'sheetApproved' | 'allApproved' | 'locked' | null;

export interface RecalcAvailabilityInput {
  readonly yearLayout: boolean;
  /** Стан аркуша на екрані (`Draft`, `Submitted`, `Approved`, …). */
  readonly state: string;
  /** Код аркуша: стани його оригіналу й копій у `sheetStates` (`КОД`, `КОД#копія`); без нього — лише `state`. */
  readonly sheetCode?: string | null | undefined;
  /** Стани аркушів документа за період (картка). */
  readonly sheetStates: Readonly<Record<string, string>> | undefined;
  /** Відповідь сервера «можна перерахувати» (контракт L3); `null`/`undefined` — сервер ще не віддає поле. */
  readonly serverCanRecalculate?: boolean | null | undefined;
  /** Чому сервер каже «ні» (`staleMonths[].lockedReason`): `approved` — затверджено; інше — закрито / не відкрито / архів. */
  readonly serverLockedReason?: string | null | undefined;
}

/**
 * Відповідь сервера з `calc-status` (`staleMonths[]`): є застарілий місяць, який перерахунок перерахує, — «можна»;
 * застарілі є, але жодного не перерахує — «ні» з причиною першого; застарілих немає — `null` (сервер нічого не каже,
 * рішення за правилом нижче).
 */
export function serverEligibility(
  staleMonths: readonly { readonly recalculable: boolean; readonly lockedReason: string | null }[] | undefined,
): { readonly canRecalculate: boolean | null; readonly lockedReason: string | null } {
  if (staleMonths === undefined || staleMonths.length === 0) return { canRecalculate: null, lockedReason: null };
  if (staleMonths.some((month) => month.recalculable)) return { canRecalculate: true, lockedReason: null };

  return { canRecalculate: false, lockedReason: staleMonths[0]?.lockedReason ?? null };
}

export function recalcBlockOf(input: RecalcAvailabilityInput): RecalcBlock {
  if (input.serverCanRecalculate !== undefined && input.serverCanRecalculate !== null) {
    if (input.serverCanRecalculate) return null;
    return input.serverLockedReason === 'approved' || input.serverLockedReason === undefined || input.serverLockedReason === null ? 'sheetApproved' : 'locked';
  }
  if (input.yearLayout) return null;
  // Сервер: «нічого рахувати» лише коли затверджено оригінал і КОЖНУ копію аркуша.
  const code = input.sheetCode ?? null;
  const own = code === null ? [] : Object.entries(input.sheetStates ?? {}).filter(([key]) => key === code || key.startsWith(`${code}#`));
  if (own.length > 0 ? own.every(([, value]) => value === 'Approved') : input.state === 'Approved') return 'sheetApproved';

  return allSheetsApproved(input.sheetStates) ? 'allApproved' : null;
}
