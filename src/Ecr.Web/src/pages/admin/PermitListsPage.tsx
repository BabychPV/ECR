import type { JSX } from 'react';
import { ListsPage } from '@/features/permits/lists/ListsPage';

/** Списки вибору пермітів (`/admin/permits/lists`, PM-08d): перелік, шторка зі значеннями, відкриття довідника/типу. */
export function PermitListsPage(): JSX.Element {
  return <ListsPage />;
}