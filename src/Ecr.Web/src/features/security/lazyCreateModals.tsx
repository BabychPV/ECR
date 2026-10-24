import { Suspense, lazy, type ComponentProps, type JSX } from 'react';
import type { CreateRoleModal } from './CreateRoleModal';
import type { CreateUserModal } from './CreateUserModal';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';

/*
 * «New role» і «New user» — лінивими чанками (D-132, бюджет маршруту `SecurityPage`): форма
 * користувача тягне `Select`/`MultiSelect`/`PasswordInput`, форма ролі — `Checkbox`/`ScrollArea`
 * і `LocalizedInput`, а потрібні вони лише після кліку.
 *
 * ⚠ Обгортки змонтовані ЗАВЖДИ, як і статичні діалоги до них: закрита Mantine `Modal` нічого не
 * малює, тож розмітка сторінки та сама, а чанк вантажиться разом зі сторінкою.
 *
 * ⛔ WCAG 2.4.3: кнопку «New role»/«New user» запам'ятовує `useRestoreFocus` ОБГОРТКИ. `returnFocus`
 * Mantine ловить відкривача лише на переході `opened` false → true усередині змонтованої `Modal`;
 * клік раніше, ніж приїхав чанк, монтує діалог уже відкритим, і після закриття фокус падав на
 * `BODY`. Коли чанк уже є, обидва повертають на ту саму кнопку.
 */
const CreateRoleModalChunk = lazy(async () => ({
  default: (await import('./CreateRoleModal')).CreateRoleModal,
}));

const CreateUserModalChunk = lazy(async () => ({
  default: (await import('./CreateUserModal')).CreateUserModal,
}));

/** `CreateRoleModal` лінивим чанком. */
export function LazyCreateRoleModal(props: ComponentProps<typeof CreateRoleModal>): JSX.Element {
  useRestoreFocus(props.opened);

  return (
    <Suspense fallback={null}>
      <CreateRoleModalChunk {...props} />
    </Suspense>
  );
}

/** `CreateUserModal` лінивим чанком. */
export function LazyCreateUserModal(props: ComponentProps<typeof CreateUserModal>): JSX.Element {
  useRestoreFocus(props.opened);

  return (
    <Suspense fallback={null}>
      <CreateUserModalChunk {...props} />
    </Suspense>
  );
}
