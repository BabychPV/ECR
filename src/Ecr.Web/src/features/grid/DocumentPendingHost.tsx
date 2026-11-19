import type { JSX } from 'react';
import { useDocumentPending } from './autosave';

/**
 * Незбережені правки документа (`D14-12`) як окремий лінивий чанк.
 *
 * ⛔ Навіщо окремо. `useDocumentPending` тягне весь `autosave.ts` (збереження зрізів, розбір відмов, правила
 * дозволів, ручні значення замість формул, ≈4 КБ gzip), а сторінка документа стояла на межі бюджету `D-132`.
 * Правки з'являються лише разом із сіткою, тож сторінка завантажує цей модуль У ТОМУ САМОМУ `Promise.all`, що й
 * `SheetWorkspace`, і монтує хост РАНІШЕ за сітку: сховище відкрите на документ до першої правки.
 */
export function DocumentPendingHost({
  documentId,
  ownerUserId,
}: {
  readonly documentId: number;
  readonly ownerUserId: number | undefined;
}): JSX.Element | null {
  useDocumentPending(documentId, ownerUserId);

  return null;
}
