import { Suspense, lazy, type ComponentProps, type JSX } from 'react';
import type { CreateRoleModal } from './CreateRoleModal';
import type { CreateUserModal } from './CreateUserModal';

/*
 * «New role» і «New user» — лінивими чанками (D-132, бюджет маршруту `SecurityPage`): форма
 * користувача тягне `Select`/`MultiSelect`/`PasswordInput`, форма ролі — `Checkbox`/`ScrollArea`
 * і `LocalizedInput`, а потрібні вони лише після кліку.
 *
 * ⚠ Обгортки змонтовані ЗАВЖДИ, як і статичні діалоги до них: закрита Mantine `Modal` нічого не
 * малює, тож розмітка сторінки та сама, чанк вантажиться разом зі сторінкою, а повернення фокуса
 * на кнопку «New role»/«New user» після закриття працює, як і було (Mantine запам'ятовує
 * відкривача на переході `opened` false → true).
 */
const CreateRoleModalChunk = lazy(async () => ({
  default: (await import('./CreateRoleModal')).CreateRoleModal,
}));

const CreateUserModalChunk = lazy(async () => ({
  default: (await import('./CreateUserModal')).CreateUserModal,
}));

/** `CreateRoleModal` лінивим чанком. */
export function LazyCreateRoleModal(props: ComponentProps<typeof CreateRoleModal>): JSX.Element {
  return (
    <Suspense fallback={null}>
      <CreateRoleModalChunk {...props} />
    </Suspense>
  );
}

/** `CreateUserModal` лінивим чанком. */
export function LazyCreateUserModal(props: ComponentProps<typeof CreateUserModal>): JSX.Element {
  return (
    <Suspense fallback={null}>
      <CreateUserModalChunk {...props} />
    </Suspense>
  );
}
