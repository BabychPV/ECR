import type { PatchCellsRequest, PatchCellsResponse } from '@/api/types';

/**
 * Хук пакетного збереження комірок.
 *
 * TODO: реалізувати:
 *  - накопичувати зміни в Map<`${rowKey}:${columnCode}`, value>;
 *  - надсилати batch-PATCH із baseVersion кожного зачепленого рядка;
 *  - новий рядок → baseVersion: null (R-B2);
 *  - розрізняти три операції (R-B4): значення, `value: null` (стерти),
 *    `isEmpty: true` (явна порожнеча). Це різні наміри користувача, і UI має
 *    давати спосіб виразити кожен: Delete → стерти, Ctrl+Delete → явна порожнеча;
 *  - після успіху оновити RowVersion із відповіді — інакше наступний патч
 *    отримає 409 на власних змінах;
 *  - оптимістичне оновлення UI з відкатом при помилці.
 */
export function useCellPatch(_documentId: number): {
  patch: (request: PatchCellsRequest) => Promise<PatchCellsResponse>;
  isPending: boolean;
} {
  throw new Error('TODO: реалізувати за описом вище');
}
