import type { JSX } from 'react';
import { BindingDetailView } from '@/features/permits/bindingsPages/BindingDetailView';

/** Прив'язка аркуша до перміта (`/admin/permits/bindings/:id`, макет §6): зіставлення колонок, лише читання. */
export function PermitBindingPage(): JSX.Element {
  return <BindingDetailView />;
}
