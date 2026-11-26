import { allSheetsApproved } from '@/features/documents/documentLock';

/**
 * ✎ DOCUX L1 / Q3 координатора: ЄДИНЕ місце рішення «чи можна перерахувати цей аркуш × період» на клієнті.
 *
 * Клієнт іде за правдою сервера. Контракт стану розрахунку (lane doc-ux-server, L3) віддає для аркуша × періоду власне
 * «можна перерахувати» — щойно поле є (`serverCanRecalculate !== undefined/null`), рішення бере ЙОГО, і правило нижче
 * не діє. Доти — правило сервера, відтворене тут (`RecalculateDocumentHandler`): поданий аркуш перераховується, «нічого
 * рахувати» — лише над ЗАТВЕРДЖЕНИМ; річна розкладка не дивиться на якір.
 */
export type RecalcBlock = 'sheetApproved' | 'allApproved' | null;

export interface RecalcAvailabilityInput {
  readonly yearLayout: boolean;
  /** Стан аркуша на екрані (`Draft`, `Submitted`, `Approved`, …). */
  readonly state: string;
  /** Стани аркушів документа за період (картка). */
  readonly sheetStates: Readonly<Record<string, string>> | undefined;
  /** Відповідь сервера «можна перерахувати» (контракт L3); `null`/`undefined` — сервер ще не віддає поле. */
  readonly serverCanRecalculate?: boolean | null | undefined;
}

export function recalcBlockOf(input: RecalcAvailabilityInput): RecalcBlock {
  if (input.serverCanRecalculate !== undefined && input.serverCanRecalculate !== null) {
    return input.serverCanRecalculate ? null : 'sheetApproved';
  }
  if (input.yearLayout) return null;
  if (input.state === 'Approved') return 'sheetApproved';

  return allSheetsApproved(input.sheetStates) ? 'allApproved' : null;
}
