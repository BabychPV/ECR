import { useEffect, useRef, useState, type JSX } from 'react';
import { Button, Divider, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type {
  ApproveSheetRequest,
  DocumentSummary,
  JobStatus,
  RecalculateDocumentRequest,
  ReopenDocumentRequest,
  SheetWorkflowRequest,
} from '@/api/types';
import { can, useSession, type MeDto } from '@/shared/session/useSession';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { outcomeOf, pollInterval } from './jobFollow';
import { humanizeJobId } from './jobLabel';
import { isAllowed, type WorkflowAction } from './transitions';
import { t } from '@/shared/i18n';

/** Аркуш, над яким виконуються дії робочого процесу. */
export interface SheetActionsProps {
  /** Документ. */
  documentId: number;
  /** Аркуш; гранулярність робочого процесу — `аркуш × період` (D-38). */
  sheetDefId: number;
  /** Період. */
  periodKey: number;
  /** Поточний стан аркуша за цей період. */
  state: string;
}

/**
 * Рівні гранта — **дзеркало** `Ecr.Domain.Enums.GrantLevel`, у тому самому
 * порядку зростання (`None`=0 … `Manage`=5).
 *
 * ⛔ Порядок і є правилом: `EditRules.CanSubmit` вимагає `>= Submit` (3), а
 * `EditRules.CanApprove` — `>= Approve` (4). Переставити тут два імені —
 * означає показати кнопку тому, кому сервер відмовить, і сховати в того, хто
 * має право.
 */
const GrantOrder = ['None', 'Read', 'Write', 'Submit', 'Approve', 'Manage'] as const;

/** Назва рівня гранта, як її віддає `/api/v1/me` (`GetCurrentUserHandler`). */
export type GrantLevelName = (typeof GrantOrder)[number];

/**
 * Ефективний рівень гранта для дій робочого процесу над аркушем.
 *
 * ⛔ Це ТОЧНЕ дзеркало `EditRules.Effective` для трьох рішень — `CanSubmit`,
 * `CanApprove`, `CanReopen`, — і саме тому воно взагалі можливе на клієнті.
 * Усі три приходять із `AccessDecisionService` з `columnDefId: 0`, а
 * `BuildContextAsync` при `columnDefId == 0` НЕ читає метадані шаблону й лишає
 * `tableDefId = 0`. Тобто ланцюг ресурсів для них — рівно
 * `Project:{id} → Sheet:{id} → Table:0 → Column:0`, а перших двох достатньо:
 * грантів на `Table:0`/`Column:0` не буває, бо нуль — не ідентифікатор.
 *
 * ⚠ Для КОМІРОК такого дзеркала немає й бути не може (`features/grid/permissions.ts`:
 * «клієнт не повторює правил доступу») — там у ланцюг входять справжні
 * `Table`/`Column`, обчислюваність колонки, вікна доступу й стан рядка, і
 * сервер тому віддає готові рішення зрізом. Тут же сервер рішення не віддає
 * зовсім, а `/api/v1/me` віддає `grants`/`denies` саме для того, щоб не
 * показувати кнопку, яка дасть 403 (`GetCurrentUserHandler`, `useSession`).
 *
 * ⚠ Невідома назва рівня — це `None`, а не «пропустимо»: розширення
 * серверного переліку не має відкривати кнопку мовчки.
 *
 * @param me Профіль із `/api/v1/me`.
 * @param projectId Проєкт документа; `null` — ще невідомий.
 * @param sheetDefId Аркуш.
 */
export function effectiveGrant(
  me: MeDto | undefined,
  projectId: number | null,
  sheetDefId: number,
): GrantLevelName {
  if (me === undefined || projectId === null) return 'None';

  /*
   * ⚠ `denies`/`grants` беруться ЗАХИЩЕНО, хоч у типі вони обов'язкові.
   * Профіль приходить мережею: старіший сервер, обрізаний проксі або заглушка
   * в тесті дають об'єкт без цих полів — і падіння тут знесло б усю шапку
   * документа через межу помилок, замість того щоб просто не показати кнопку.
   * Відсутнє поле = немає гранта = `None`: відмова закрита, а не відкрита.
   */
  const denies: readonly string[] = me.denies ?? [];
  const grants: Readonly<Record<string, string>> = me.grants ?? {};

  // Від найширшого до найдрібнішого — той самий масив, що в `EditRules.Effective`.
  const scopes = [`Project:${projectId}`, `Sheet:${sheetDefId}`, 'Table:0', 'Column:0'];

  // Заборона перемагає на будь-якому рівні (ФВ-6.6).
  if (scopes.some((scope) => denies.includes(scope))) return 'None';

  // Дозвіл — з найдрібнішого ОГОЛОШЕНОГО рівня: грант на аркуш перекриває
  // грант на проєкт, і саме так права звужують точково.
  for (let i = scopes.length - 1; i >= 0; i--) {
    const level: string | undefined = grants[scopes[i] ?? ''];
    if (level === undefined) continue;

    return (GrantOrder as readonly string[]).includes(level) ? (level as GrantLevelName) : 'None';
  }

  return 'None';
}

/** Чи дотягує наявний рівень до потрібного. */
export function meetsGrant(actual: GrantLevelName, required: GrantLevelName): boolean {
  return GrantOrder.indexOf(actual) >= GrantOrder.indexOf(required);
}

/**
 * Поріг рівня, з якого СЕРВЕР приймає дію.
 *
 * ⛔ `reject` — теж `Approve`, і це не описка: відхилення йде тим самим
 * `POST /documents/{id}/approve` з `approved: false`, тобто через
 * `ApproveSheetHandler` → `CanApproveAsync` → `EditRules.CanApprove`. Порогів
 * у сервера два, а не три.
 */
const RequiredGrant: Readonly<Record<'submit' | 'approve' | 'reject', GrantLevelName>> = {
  submit: 'Submit',
  approve: 'Approve',
  reject: 'Approve',
};

/**
 * Робочий процес аркуша: подати, затвердити, відхилити, повернути, перерахувати.
 *
 * ⛔ Компонент з'явився після аудиту, і знахідка була найдорожчою за проєкт:
 * **із сорока дій запису дев'ятнадцять не мали в інтерфейсі жодної кнопки**, і
 * серед них `POST /documents/{id}/approve`. Тобто робочий процес обривався на
 * поданні: документ можна було подати і не можна затвердити, а без `Approved`
 * дані не стають дійсними (`ФВ-5.14`) і не потрапляють у звіти для регулятора
 * (`ФВ-10.11`).
 *
 * ⚠ Тести API при цьому були зелені й праві: вони доводять, що ендпоінт
 * працює, а не що до нього веде кнопка. Жоден зі сторожів цього не бачив за
 * побудовою — усі йшли від клієнта до сервера. Тепер є тринадцятий, який іде
 * назустріч.
 *
 * ⛔ Кнопка показується за ДВОМА умовами: стан аркуша (`transitions.ts`) і
 * право. Стан каже, чи є що робити; право — чи цій людині можна. Маршрут
 * погодження рахує сервер (`IAccessDecisionService.CanApproveAsync`), і
 * клієнт його не відтворює: друга реалізація правил доступу розійшлася б із
 * першою і показувала б дозвіл там, де сервер відмовляє.
 *
 * ⚠ «Право» тут — це і функціональне право (`permissions`, напр.
 * `Calculation.Recalculate`), і РІВЕНЬ ГРАНТА на ресурс (`grants`/`denies`):
 * робочий процес сервер закриває саме рівнем, а не іменованим правом — див.
 * `effectiveGrant` нижче. Клієнт відтворює рівно ту частину рішення, яка
 * НЕ залежить від бази (гранти вже в `/api/v1/me`); решта — стан періоду,
 * помилки валідації, черга кроку маршруту — лишається за сервером, і кнопка,
 * яка через них відмовить, показує причину відмовою, а не зникненням.
 */
export function SheetActions({
  documentId,
  sheetDefId,
  periodKey,
  state,
}: SheetActionsProps): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  // Яка дія чекає на причину; `null` — діалог закритий.
  const [asking, setAsking] = useState<'reject' | 'reopen' | null>(null);

  /** Перечитує стан документа після кожної зміни робочого процесу. */
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
  };

  const submit = useMutation({
    mutationFn: () =>
      apiFetch(`/api/v1/documents/${documentId}/submit`, {
        method: 'POST',
        body: JSON.stringify({ sheetDefId, periodKey } satisfies SheetWorkflowRequest),
      }),
    onSuccess: async () => {
      await refresh();
      showDone(t('document.submitted'));
    },
    // ⚠ Причина показується як є: Submit при осиротілих рядках
    // (`ECR-SUB-4221`) — це не «помилка сервера», а перелік того, що треба
    // виправити.
    onError: showApiError,
  });

  const decide = useMutation({
    mutationFn: (verdict: { approved: boolean; reason: string | null }) =>
      apiFetch(`/api/v1/documents/${documentId}/approve`, {
        method: 'POST',
        body: JSON.stringify({
          sheetDefId,
          periodKey,
          approved: verdict.approved,
          reason: verdict.reason,
        } satisfies ApproveSheetRequest),
      }),
    onSuccess: async (_result, verdict) => {
      await refresh();
      setAsking(null);
      showDone(verdict.approved ? t('workflow.approved') : t('workflow.rejected'));
    },
    onError: showApiError,
  });

  const reopen = useMutation({
    mutationFn: (reason: string) =>
      apiFetch(`/api/v1/documents/${documentId}/reopen`, {
        method: 'POST',
        body: JSON.stringify({ sheetDefId, periodKey, reason } satisfies ReopenDocumentRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setAsking(null);
      showDone(t('workflow.reopened'));
    },
    // ⚠ Найчастіша відмова тут — `ECR-PRD-4223`: період закрито, і спершу
    // треба відкрити період, а це інше право (`D-67`). Текст веде саме туди.
    onError: showApiError,
  });

  /**
   * Перерахунок: постановка в чергу і **стеження за нею**.
   *
   * ⛔ Тут була одна нотифікація «поставлено в чергу як `4f2c…`» — і на цьому
   * все (директива №09 `W8` п.7). Перерахунок іде у фон, і зворотного зв'язку
   * не було жодного: оператор не дізнавався ні коли числа оновилися, ні що
   * задача впала. Він бачив GUID і не мав куди його ввести — той самий
   * дефект, який експорт уже пройшов (`ExportButton.tsx`), і виправлений тим
   * самим прийомом.
   *
   * ⚠ Сітка перечитується САМЕ на завершенні, а не на постановці в чергу:
   * оновити її одразу означало б показати старі числа під написом
   * «перераховано».
   *
   * ── Стан: задача РАЗОМ із адресою, для якої її поставили ───────────────
   *
   * ⛔ Аудит 2026-09-16 §10.6: тут жив самотній `recalcJobId`, а ефект
   * завершення (нижче) замикався на ПОТОЧНИХ пропах `documentId`/`periodKey` —
   * не на тих, що були активні при постановці в чергу. Компонент не
   * перемонтовується при перемиканні аркуша/періоду (`DocumentPage.tsx`
   * рендерить його без `key`), тож оператор, що на 202401 натиснув
   * «Перерахувати» й перейшов на 202402, отримував три тихі наслідки:
   * (1) кнопка на 202402 крутилася на задачі, якої там ніхто не ставив, і
   * повторний перерахунок цього періоду був недоступний; (2) тост
   * «перерахунок завершено» з'являвся над ЧУЖИМ періодом без жодної згадки,
   * якого він; (3) інвалідувався кеш `['document', id, 202402]` замість
   * `202401` — період, що справді перерахувався, лишався зі старими числами.
   *
   * ⚠ Стеження НЕ обривається при переході (це був би четвертий наслідок,
   * протилежний): задача триває, і її завершення однаково має оновити кеш
   * СВОГО періоду. Від поточного екрана залежить лише вигляд КНОПКИ.
   *
   * ⚠ Слот ОДИН, і це свідомо. Розблокована кнопка на іншому періоді дає
   * оператору поставити другий перерахунок, поки перший іде, — і тоді клієнт
   * перестає стежити за першим: його кеш оновиться не на завершенні, а
   * звичайним `staleTime` (30 с, `App.tsx`) при повторному заході. Ціна
   * несумірна з попередньою поведінкою, де кнопка була заблокована на ВСІХ
   * періодах і другий перерахунок був недосяжний узагалі; черга задач на
   * клієнті — окрема задача, не ця.
   */
  const [recalc, setRecalc] = useState<{
    jobId: string;
    documentId: number;
    sheetDefId: number;
    periodKey: number;
  } | null>(null);

  const recalcJobId = recalc?.jobId ?? null;

  const recalculate = useMutation({
    mutationFn: () =>
      // ⛔ Q-331: `sheetDefId` тепер справді звужує перерахунок до ЦЬОГО
      // аркуша (директива паритету зі старою системою, прогалина 2) — до
      // цього пакета кнопка передавала лише `periodKey`, і сервер
      // перераховував увесь документ незалежно від того, з якого аркуша її
      // натиснули.
      apiEnqueue(`/api/v1/documents/${documentId}/recalculate`, {
        periodKey,
        sheetDefId,
      } satisfies RecalculateDocumentRequest),
    onSuccess: (job) => {
      // ⚠ Адреса фіксується САМЕ ТУТ — у мить, коли задача стала в чергу, — і
      // далі не змінюється, хай оператор ходить по періодах скільки хоче.
      setRecalc({ jobId: job.jobId, documentId, sheetDefId, periodKey });
      // ⛔ Аудит-пас 8, lane6, п.8: людський вигляд у ТОСТІ, `jobId` у стані
      // (`setRecalcJobId`) — і, отже, в запиті опитування нижче — не
      // змінюється.
      showDone(t('workflow.recalcQueued', { job: humanizeJobId(job.jobId) }));
    },
    onError: showApiError,
  });

  /**
   * ⛔ Захист від подвійного кліку — СИНХРОННИЙ, не через `recalculate.isPending`.
   * Mantine `Button` вимикається лише разом із `loading`, а той оновлюється
   * лише на НАСТУПНОМУ рендері React — швидкий подвійний клік встигає
   * викликати `mutate()` двічі ДО першого перерендеру, і ставить у чергу два
   * однакових перерахунки. `useRef` читається й пишеться негайно, у тому
   * самому обробнику, без очікування на React.
   */
  const recalculateInFlight = useRef(false);

  const recalcJob = useQuery({
    queryKey: ['job', recalcJobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(recalcJobId ?? '')}`),
    enabled: recalcJobId !== null,

    // ⚠ Правило опитування — у чистому модулі `jobFollow.ts`: саме його не
    // було, і саме його треба перевіряти окремо від компонента.
    refetchInterval: (query) => pollInterval(query.state.data?.state),

    // ⚠ `retry: false` і мовчазна зупинка на відмові: `GET /jobs/{id}` вимагає
    // окремого права (`System.ViewHealth`, `Q-156`), і оператор без нього має
    // лишитися з поставленою задачею, а не з червоним сповіщенням про право,
    // якого він не просив.
    retry: false,
  });

  /*
   * ⛔ Стан задачі, якого НЕ ВДАЛОСЯ прочитати, — це не «ще виконується».
   * `GET /jobs/{id}` вимагає окремого права (`System.ViewHealth`, `Q-156`), і
   * без нього кнопка крутилася б вічно: оператор бачив би «перераховується»
   * на задачі, стан якої йому просто не показують. Тому відмова читання
   * зупиняє стеження мовчки — задача поставлена, і про це вже сказано.
   */
  const outcome = recalcJobId === null
    ? null
    : outcomeOf(recalcJob.data?.state, recalcJob.isError);

  // ⛔ §10.6: «виконується» — про ЦЕЙ екран, а не про будь-яку задачу в
  // пам'яті компонента. Кнопка на іншому аркуші/періоді мусить бути звичайною
  // й натискабельною: там нічого не поставлено, і заборонити його перерахунок
  // означало б показати заблоковану кнопку без жодної причини.
  const recalcIsHere =
    recalc !== null &&
    recalc.documentId === documentId &&
    recalc.sheetDefId === sheetDefId &&
    recalc.periodKey === periodKey;

  const recalcRunning = recalcIsHere && outcome === 'running';

  // ⛔ Про КІНЕЦЬ повідомляється рівно один раз на задачу: опитування триває
  // кілька тактів після завершення (React Query віддає ті самі дані з кешу), і
  // без цієї позначки оператор отримав би серію однакових сповіщень.
  const reported = useRef<string | null>(null);

  useEffect(() => {
    if (recalc === null || outcome === null || outcome === 'running') return;

    // Стан прочитати не вдалося — мовчимо: задача поставлена, і про це вже
    // сказано; повідомляти про право, якого оператор не просив, нема сенсу.
    if (outcome === 'unknown') return;
    if (reported.current === recalc.jobId) return;

    reported.current = recalc.jobId;

    if (outcome === 'succeeded') {
      // ⛔ §10.6: період — У ТЕКСТІ тосту. «Перерахунок завершено», показане
      // над іншим періодом (оператор перейшов, поки задача йшла), стверджує
      // неправду про те, на що людина дивиться. Нового рядка каталогу тут не
      // заводиться — каталог живе в сіді БД, поза цим пакетом, — тому період
      // дописується до наявного рядка тим самим `·`, яким уже підписано
      // діалоги (`UserAccessEditor.tsx`).
      showDone(`${t('workflow.recalcDone')} · ${String(recalc.periodKey)}`);

      // ⚠ Сітка перечитується САМЕ тут: оновити її на постановці в чергу
      // означало б показати старі числа під написом «перераховано».
      //
      // ⛔ І саме за ЗАХОПЛЕНОЮ адресою, а не за поточними пропами: інакше
      // оновлення дістається періоду, який не перераховували, а той, що
      // перерахувався, лишається зі старими числами назавжди (до перезаходу).
      void queryClient.invalidateQueries({ queryKey: ['table-slice'] });
      void queryClient.invalidateQueries({
        queryKey: ['document', recalc.documentId, recalc.periodKey],
      });

      return;
    }

    // ⛔ Q-234: `error`, а не `message` — та сама плутанина полів, що й на
    // `PeriodsPage`/`ExportButton`. `Message` — останній прогрес
    // (`IJobProgress.ReportAsync`), на відмові він лишається тим, яким був
    // до неї; причину відмови несе `Error` (`FinishAsync`, `QuartzJobAdapter.cs`).
    notifications.show({
      color: 'statusError',
      message: recalcJob.data?.error ?? t('workflow.recalcFailed'),
    });
  }, [recalc, outcome, recalcJob.data?.error, queryClient]);

  const me = session.data;

  /*
   * ⛔ F9 (`docs/build/UI-WALKTHROUGH.md`). До цього «Submit», «Approve» і
   * «Reject» показувалися ЛИШЕ за станом аркуша, тоді як сусідні
   * «Recalculate» і «Return for edits» — ще й за правом. Наслідок зі знімка
   * `07-document-open.png`: оператор із грантом `Write` на проєкт бачив синю
   * кнопку «Submit», а сервер на неї відповідає `ECR-ACCS-0403`
   * (`EditRules.CanSubmit`: поріг `GrantLevel.Submit`).
   *
   * ⚠ Проєкт береться з УЖЕ прочитаного кеша, а не новим запитом: `DocumentPage`
   * тримає `['document', id, periodKey]` (той самий ключ, що інвалідує `refresh`
   * вище) і рендерить цей компонент лише всередині власного `AsyncBoundary`,
   * тобто коли `DocumentSummary` вже є. Порожній кеш — це `null`, тобто
   * `None`, тобто кнопки немає: відмова закрита, а не відкрита.
   */
  const summary = queryClient.getQueryData<DocumentSummary>([
    'document',
    documentId,
    periodKey,
  ]);

  const grant = effectiveGrant(me, summary?.projectId ?? null, sheetDefId);

  /**
   * Чи пропустить сервер дію робочого процесу цієї людини.
   *
   * ⚠ Симуляція відмовляється ПЕРШОЮ — так само, як у сервера: `CanSubmit`,
   * `CanApprove` і `CanReopen` починаються з `profile.IsSimulation` →
   * `SimulationReadOnly`. Адміністратор, який дивиться чужими правами
   * (`ФВ-6.16a`), не пише від чужого імені.
   */
  const mayWorkflow = (action: 'submit' | 'approve' | 'reject'): boolean =>
    me !== undefined && !me.isSimulation && meetsGrant(grant, RequiredGrant[action]);

  const canSubmit = isAllowed('submit', state) && mayWorkflow('submit');
  const canApprove = isAllowed('approve', state) && mayWorkflow('approve');
  const canReject = isAllowed('reject', state) && mayWorkflow('reject');

  // ⛔ Аудит Етапу 3, лана "Documents core" (`lane3-workflow-buttons-not-grouped`,
  // знахідка людини зі скріншотом): `DocumentPage.tsx` рендерить ОДИН
  // пласкій `Group`, що несе Period/Validate/Import/Export і — до цього
  // фіксу — ВКЛАДЕНИЙ `<Group>` цього компонента (Recalculate/Submit/
  // Approve/Reject/Reopen). Вкладена Group у флекс-контейнері поводиться
  // як ОДИН елемент переносу, тож рядок ламався нерівномірно залежно від
  // того, скільки кнопок показано (права/стан аркуша різні для кожного
  // користувача) — «кнопки не згруповані та не в одному ряду».
  //
  // ⚠ Фікс — без Group ТУТ: кнопки повертаються ПРЯМИМИ дітьми фрагмента,
  // тобто прямими flex-елементами БАТЬКІВСЬКОГО `Group` (`DocumentPage.tsx`),
  // а не вкладеним контейнером. `Divider` перед ними — видимий кордон між
  // «безпечними» діями (Period/Validate/Export/Import) і «робочим процесом»
  // (Recalculate/Submit/Approve/Reject/Reopen, останні три вже кольорові:
  // green/statusError) — те саме розділення класів ризику, що директива вже
  // застосувала для лан 1-7.
  const hasAnyAction =
    can(me, 'Calculation.Recalculate') ||
    canSubmit ||
    canApprove ||
    canReject ||
    (isAllowed('reopen', state) && can(me, 'Document.Reopen'));

  return (
    <>
      {hasAnyAction && (
        // ⚠ Роздільник — лише коли є ЩО розділяти: порожній `Divider` без
        // жодної кнопки за ним (роль без жодного права робочого процесу,
        // напр. Auditor) виглядав би як зламаний хвіст рядка.
        <Divider orientation="vertical" />
      )}
      {/*
       * ⛔ Прогалина 2 директиви паритету зі старою системою (Q-327 →
       * Q-331): до Q-331 кнопка стояла на екрані ОДНОГО аркуша
       * (`SheetActions` отримує `sheetDefId`), а `POST
       * /documents/{id}/recalculate` перераховувала ВЕСЬ документ — усі
       * аркуші за цей період, не лише активний. Тепер `sheetDefId`
       * справді йде в тілі запиту, і сервер звужує ЗАПИС до таблиць цього
       * аркуша. Підказка лишається (текст оновлено), бо нюанс і досі є:
       * формула цього аркуша має право читати дані сусіднього, тож
       * перерахунок однаково враховує весь документ на ВХОДІ, хоч і пише
       * лише в цей аркуш. Підпис кнопки лишається нейтральним «Recalculate»
       * (той самий, що й на екрані проєкту, `PeriodsPage.tsx`).
       */}
      {can(me, 'Calculation.Recalculate') && (
        <Tooltip label={t('workflow.recalculateHint')} multiline w={260}>
          <Button
            size="xs"
            variant="default"
            loading={recalculate.isPending || recalcRunning}
            onClick={() => {
              if (recalculateInFlight.current) return;
              recalculateInFlight.current = true;
              recalculate.mutate(undefined, {
                onSettled: () => {
                  recalculateInFlight.current = false;
                },
              });
            }}
          >
            {recalcRunning ? t('workflow.recalcRunning') : t('workflow.recalculate')}
          </Button>
        </Tooltip>
      )}

      {canSubmit && (
        <Button size="xs" loading={submit.isPending} onClick={() => submit.mutate()}>
          {t('document.submit')}
        </Button>
      )}

      {/* ⛔ Затвердження і відхилення — пара, і показуються разом. Кнопка
          «Затвердити» без «Відхилити» перетворює погодження на формальність:
          єдиний спосіб не затвердити — не натиснути нічого, і аркуш висить
          у `Submitted` без жодного сліду причини. */}
      {canApprove && (
        <Button
          size="xs"
          color="green"
          loading={decide.isPending}
          onClick={() => decide.mutate({ approved: true, reason: null })}
        >
          {t('workflow.approve')}
        </Button>
      )}

      {canReject && (
        <Button size="xs" color="statusError" variant="light" onClick={() => setAsking('reject')}>
          {t('workflow.reject')}
        </Button>
      )}

      {/* ⚠ Повернення в роботу — окреме небезпечне право (`ФВ-6.12`): воно
          дає змогу змінити вже подані числа. Тому і кнопка окрема, і
          причина обов'язкова.

          ⚠ Тут навмисно ЛИШЕ право, без порога гранта, хоч сервер перевіряє
          обидва (`ReopenDocumentHandler`: `profile.Has("Document.Reopen")`,
          далі `CanReopenAsync` → `EditRules.CanReopen` з порогом
          `GrantLevel.Approve`). Це не та розбіжність, про яку F9: кнопка вже
          закрита правом, і вужчою за сервер вона не стає. Довести її до
          другої умови — окремий крок: `e2e/security.spec.ts` проводить
          `Document.Reopen` як штатну дію для приведення аркуша в `Draft`
          (`makeSheetEditable`), і зміна порога тут зачіпає той прохід. */}
      {isAllowed('reopen', state) && can(me, 'Document.Reopen') && (
        <Button size="xs" variant="light" onClick={() => setAsking('reopen')}>
          {t('workflow.reopen')}
        </Button>
      )}

      <ReasonModal
        opened={asking === 'reject'}
        title={t('workflow.rejectTitle')}
        label={t('workflow.reason')}
        description={t('workflow.rejectHint')}
        confirmLabel={t('workflow.reject')}
        isPending={decide.isPending}
        onConfirm={(reason) => decide.mutate({ approved: false, reason })}
        onClose={() => setAsking(null)}
      />

      <ReasonModal
        opened={asking === 'reopen'}
        title={t('workflow.reopenTitle')}
        label={t('workflow.reason')}
        description={t('workflow.reopenHint')}
        confirmLabel={t('workflow.reopen')}
        isPending={reopen.isPending}
        onConfirm={(reason) => reopen.mutate(reason)}
        onClose={() => setAsking(null)}
      />
    </>
  );
}

/** Чи має аркуш у цьому стані бути доступним для правки. */
export function isEditable(state: string): boolean {
  // Подане і затверджене не редагується: щоб змінити числа, аркуш повертають
  // у роботу окремою дією з причиною (`ФВ-5.20a`).
  return !isAllowed('approve', state) && !isAllowed('reopen', state);
}

export type { WorkflowAction };
