import { lazy, Suspense, useId, useState, type JSX } from 'react';
import { Menu, Stack, Text } from '@mantine/core';
import type { DocumentSummary } from '@/api/types';
import { can, useSession, type MeDto } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { hasProjectGrant } from './projectGrant';
import { t } from '@/shared/i18n';
import { ChangeDocumentKeyPermission } from './api';

/**
 * Мутація, поля форми й діалог — окремим чанком (`D-132`, `BusinessKeyChangeHost`); вантажиться з першим відкриттям,
 * на наведенні на пункт — заздалегідь. Стиль — `loadX`/`lazy(async () => ...)`, як у `DocumentPage.tsx`.
 */
const loadBusinessKeyChangeHost = () => import('./BusinessKeyChangeHost');
const BusinessKeyChangeHost = lazy(loadBusinessKeyChangeHost);

/**
 * Чи має користувач ефективний грант **Write** (чи вищий) на проєкт.
 *
 * ⛔ Дзеркало серверного `GetCurrentUserHandler.LevelForProject` /
 * `AccessProfile.LevelFor(ResourceKind.Project, …)` — рівно того самого
 * рішення, яким `ChangeDocumentKeyHandler` перевіряє грант перед записом
 * (`profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write`).
 * Не сходи `SheetActions.effectiveGrant`: та функція будує ланцюг
 * `Project → Sheet → Table:0 → Column:0`, потрібний ЛИШЕ трьом рішенням
 * робочого процесу, що йдуть через `AccessDecisionService` з `columnDefId: 0`
 * (сам файл це пояснює). Зміна ключа документа таким ланцюгом не йде: сервер
 * дивиться РІВНО на `Project:{id}`, без запасних рівнів, — і клієнтська
 * перевірка звужена так само навмисно, а не для економії коду.
 *
 * ⚠ Заборона виграє на будь-якому рівні (ФВ-6.6), як і скрізь: `denies`
 * перевіряється ПЕРШИМ.
 */
export function hasProjectWriteGrant(me: MeDto | undefined, projectId: number): boolean {
  return hasProjectGrant(me, projectId, 'Write');
}

// `hasProjectGrant` — у `projectGrant.ts` (без рядка дій аркуша; бюджет маршрутів, D-132); реекспорт для наявних імпортів.
export { hasProjectGrant } from './projectGrant';

/**
 * Чи документ, НАСКІЛЬКИ ЙОГО ЗНАЄ КЛІЄНТ, має поданий або погоджений аркуш.
 *
 * ⛔ Дзеркало `DocumentKeyChange.EnsureChangeable`: локер лише подання й
 * погодження (`Submitted`/`Approved`) — відхилений аркуш і чернетка й далі в
 * роботі, зміна ключа їх не чіпає.
 *
 * ⚠ `sheetStates` завжди приходить разом із `DocumentSummary` (як і в
 * `isKnownDraft`, `DeleteDocumentAction.tsx`), тож «стан невідомий» тут не
 * трапляється, доки сам документ не завантажився: аркуш без запису стану —
 * `Draft`, тобто не заблокований.
 */
export function hasLockedSheet(sheetStates: Readonly<Record<string, string>>): boolean {
  return Object.values(sheetStates).some((state) => state === 'Submitted' || state === 'Approved');
}

interface BusinessKeyChangeActionArgs {
  readonly documentId: number;

  /** `undefined` — документ ще не приїхав: кнопки немає, немає й чинного ключа. */
  readonly document: DocumentSummary | undefined;

  /** Період — частина ключа кешу документа (R-A6), потрібен для інвалідизації. */
  readonly periodKey: number;
}

interface BusinessKeyChangeAction {
  /**
   * Пункт меню «More» сторінки документа; `null`, якщо права немає.
   *
   * ⛔ Не кнопка в рядку дій: зміна ключа — рідкісна дія з аудитом, і серед
   * щоденних вона лише переповнювала рядок (знімок людини, 1290 px).
   */
  readonly menuItem: JSX.Element | null;

  /** Діалог — ОКРЕМО від пункту: меню розмонтовує вміст, щойно закривається. */
  readonly dialog: JSX.Element | null;

  /**
   * Банер відмови `rekeyStale` — під шапкою, ПІСЛЯ закриття діалогу.
   *
   * ⚠ Решта відмов (409 duplicate/locked, 422 reason/key) лишаються
   * УСЕРЕДИНІ діалогу — форму, яку можна виправити й надіслати знову,
   * закривати немає сенсу (той самий вибір, що в `security/UserAdminActions.tsx`
   * для скидання пароля: невдалий пароль лишає діалог відкритим). `rekeyStale`
   * інша: значення, з яким пішов запит, застаріле, а не невалідне, — форму
   * повторно надсилати нема чим, доки не прочитано новий ключ.
   */
  readonly refusal: JSX.Element | null;
}

/**
 * Дія «Змінити номер справи» (бізнес-ключ документа, ФВ-3.9) на сторінці
 * документа.
 *
 * ⛔ Право `Document.ChangeKey` — небезпечне: шаблонні роздачі сіду його не
 * дають, поіменно його має лише `SystemAdministrator` (секція сіду
 * `FP:sysadmin-rights`, рішення людини 12.10) і ролі, створені адміністратором:
 * кнопка з'являється лише тоді, коли право видали, і разом із ним — грант Write на проєкт
 * (`hasProjectWriteGrant`), як того вимагає сервер.
 *
 * ⚠ Аркуші поданий/погоджений — кнопка вимкнена ЗАЗДАЛЕГІДЬ, з видимою
 * причиною поруч (той самий прийом, що `DataSourceDrawer.tsx` для
 * `sourceEntities > 0 || collectionSchedules > 0`): лічильники клієнта могли
 * застаріти, серверна відмова `409 rekeyLocked` лишається остаточною.
 */
export function useBusinessKeyChangeAction({
  documentId,
  document,
  periodKey,
}: BusinessKeyChangeActionArgs): BusinessKeyChangeAction {
  const session = useSession();
  const me = session.data;

  const [opened, setOpened] = useState(false);
  // ⚠ Чанк діалогу — лише після першого відкриття; потім діалог лишається змонтованим, щоб закриття
  // йшло тим самим переходом `Modal`, що й доти.
  const [everOpened, setEverOpened] = useState(false);
  if (opened && !everOpened) setEverOpened(true);
  // `rekeyStale` від хоста: банер рисуємо тут, поза діалогом.
  const [staleError, setStaleError] = useState<unknown>(null);

  const lockedReasonId = useId();

  const allowed =
    can(me, ChangeDocumentKeyPermission)
    && document !== undefined
    && hasProjectWriteGrant(me, document.projectId);

  const locked = document !== undefined && hasLockedSheet(document.sheetStates);

  /*
   * ⚠ Заблокований пункт лишається ВИДИМИМ і пояснює причину під підписом —
   * той самий прийом, що й кнопка до переїзду в меню: сховане без пояснення
   * читалося б як «права немає», а причина тут — стан аркушів.
   */
  const menuItem = allowed ? (
    <Menu.Item
      disabled={locked}
      // ⚠ Ім'я пункту — лише підпис дії: причина блокування стоїть усередині
      // того самого `<button>` і без явного імені склеїлася б із ним. Причину
      // читач чує через `aria-describedby`.
      aria-label={t('documents.changeKey')}
      aria-describedby={locked ? lockedReasonId : undefined}
      onClick={() => setOpened(true)}
      onPointerEnter={() => void loadBusinessKeyChangeHost()}
      data-change-business-key=""
    >
      {/* ⚠ Лише `span`-и: пункт меню — це `<button>`, і блоковий `div`/`p`
          усередині нього — невалідна вкладеність. */}
      <span style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <span>{t('documents.changeKey')}</span>
        {locked && (
          <Text
            component="span"
            id={lockedReasonId}
            size="xs"
            c="dimmed"
            maw={260}
            data-change-key-blocked-reason=""
          >
            {t('documents.changeKeyLockedHint')}
          </Text>
        )}
      </span>
    </Menu.Item>
  ) : null;

  const dialog =
    allowed && everOpened && document !== undefined ? (
      <Suspense fallback={null}>
        <BusinessKeyChangeHost
          documentId={documentId}
          document={document}
          periodKey={periodKey}
          opened={opened}
          onClose={() => setOpened(false)}
          onRefusal={setStaleError}
        />
      </Suspense>
    ) : null;

  const refusal =
    staleError !== null ? (
      <Stack gap="xs" data-change-key-stale="">
        <Text size="sm" c="statusError">
          {t('documents.changeKeyStaleHint')}
        </Text>
        <ErrorAlert error={staleError} />
      </Stack>
    ) : null;

  return { menuItem, dialog, refusal };
}
