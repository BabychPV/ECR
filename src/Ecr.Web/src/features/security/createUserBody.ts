import type { CreateUserRequest } from '@/api/types';

/**
 * Тіло запиту на створення користувача.
 *
 * ⛔ Винесене з компонента ОКРЕМОЮ функцією навмисно — так само, як тіло
 * створення проєкту після `A7-56`. Дефекти жили саме тут, і всі три мовчазні:
 *
 * `A7-60`: `initialPassword` був жорстко `null`, а обробник вимагає разовий
 * пароль для локального запису (`ECR-CELL-0422`). Вибір «Local» + «Зберегти»
 * давав `422` ЗАВЖДИ — локальний обліковий запис завести було неможливо.
 *
 * `A7-61`: `roleCodes` теж був `null`, тобто запис створювався **без жодної
 * ролі**, а призначити її потім не було чим. Обліковий запис виходив
 * працездатним на вигляд і безправним насправді.
 *
 * `A7-62`: адреси не було ані в тілі, ані у формі, тож `NotificationJob`
 * завжди отримував порожній перелік адресатів.
 *
 * ⚠ Разовий пароль ЙДЕ в запиті — так само, як у вході і зміні пароля.
 * Сервер його не генерує і не має: повертати пароль у відповіді API прямо
 * заборонено (`D-11`, `ФВ-6.11`), а надіслати листом нікуди — адреси в щойно
 * створеного запису ще немає. Ризик того, що адміністратор знає пароль,
 * знімається обов'язковою зміною при першому вході (`ФВ-6.18`).
 */
export function createUserBody(form: {
  userName: string;
  displayName: string;
  provider: string;
  sid: string;
  oneTimePassword: string;
  email: string;
  roleCodes: string[];
}): CreateUserRequest {
  const local = form.provider === 'Local';

  return {
    userName: form.userName.trim(),
    displayName: form.displayName.trim().length === 0 ? null : form.displayName.trim(),
    provider: form.provider,

    // ⚠ SID потрібен саме доменному запису: за ним, а не за іменем, сервер
    // упізнає користувача після перейменування в каталозі.
    sid: form.provider === 'Windows' && form.sid.trim().length > 0 ? form.sid.trim() : null,

    initialPassword: local ? form.oneTimePassword : null,
    roleCodes: form.roleCodes.length === 0 ? null : form.roleCodes,
    email: form.email.trim().length === 0 ? null : form.email.trim(),
  };
}
