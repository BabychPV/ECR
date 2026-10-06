import { useEffect, useRef, useState, type JSX } from 'react';
import { Button, Divider, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch, EcrApiError } from '@/api/client';
import type {
  ApproveSheetRequest,
  DocumentSummary,
  JobStatus,
  RecalculateDocumentRequest,
  ReopenDocumentRequest,
  SheetWorkflowRequest,
} from '@/api/types';
import { hasLockedSheet, locksDataActions, type DocumentLock } from '@/features/documents/documentLock';
import { invalidateSlices } from '@/features/grid/sliceCache';
import { JobFailure } from '@/features/jobs/JobFacts';
import { can, useSession, type MeDto } from '@/shared/session/useSession';
import { LazyConfirmModal, LazyReasonModal } from './lazyDialogs';
import { Hint } from '@/shared/ui/Hint';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useRecallAvailability, type RecallSheetRequest } from './api';
import { outcomeOf, pollInterval } from './jobFollow';
import { useSettledAction } from '@/features/grid/settleEdits';
import { isRecalculateKey, isTypingOrDialogTarget } from '@/features/grid/shortcutKey';
import { humanizeJobId } from './jobLabel';
import { isAllowed, type WorkflowAction } from './transitions';
import { t } from '@/shared/i18n';

/**
 * Відмова дії над аркушем — з назвою аркуша (A2-08), окремим чанком.
 *
 * ⚠ Lazy: модуль потрібен лише на відмові, а `SheetActions` сидить у бюджеті
 * `DocumentPage`/`PeriodsPage` (ліміт 250 КБ). Не довантажився — текст сервера.
 */
function showSheetError(error: unknown, sheetName: string | null): void {
  import('./sheetDenial').then(
    (module) => module.showSheetError(error, sheetName),
    () => showApiError(error),
  );
}

/** Аркуш, над яким виконуються дії робочого процесу. */
interface SheetActionsProps {
  /** Документ. */
  documentId: number;
  /** Аркуш; гранулярність робочого процесу — `аркуш × період` (D-38). */
  sheetDefId: number;
  /** Період. */
  periodKey: number;
  /** Поточний стан аркуша за цей період. */
  state: string;

  /**
   * Назва аркуша мовою інтерфейсу — для відмов `ECR-ACCS-0403` (A2-08):
   * «аркуш «Викиди» не можна…», а не «аркуш 2». Не задано — текст сервера.
   */
  sheetName?: string | null | undefined;

  /**
   * Чому документ за цей період не змінити (`F-18`): архівний проєкт, закритий
   * чи ще не відкритий період. `null`/не задано — нічого не заважає.
   */
  lock?: DocumentLock | null | undefined;
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

  /*
   * ⛔ S2 — та сама передумова, що в `EditRules.Effective`: грант на аркуш
   * діє лише в проєкті, який користувач бачить (грант на проєкт ≥ `Read`).
   * Id аркуша належить ВЕРСІЇ ШАБЛОНУ, спільній для всіх проєктів шаблону;
   * без цієї умови `Sheet:S = Approve` «з проєкту A» показав би кнопку в B.
   * `None` і невідома назва рівня передумовою не є.
   */
  const projectLevel = grants[`Project:${projectId}`];
  if (projectLevel === undefined || (GrantOrder as readonly string[]).indexOf(projectLevel) < GrantOrder.indexOf('Read')) {
    return 'None';
  }

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
  sheetName = null,
  lock = null,
}: SheetActionsProps): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  // Яка дія чекає на причину; `null` — діалог закритий.
  const [asking, setAsking] = useState<'approve' | 'reject' | 'reopen' | 'recall' | null>(null);

  /** Перечитує стан документа після кожної зміни робочого процесу. */
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
    // ⛔ AN-28/L8-02: `cellPermissions` зрізу залежать від стану аркуша
    // (`EditRules.CanEdit` -> `DocumentSubmitted`), а зріз живе 5 хв без
    // перезапиту на фокус. Без цього після Recall/Return/Reopen сітка лишалась
    // сірою до F5. Зрізи ЦЬОГО аркуша перезапитуються, решта лише позначається.
    await invalidateSlices(queryClient, { documentId, periodKey, sheetDefId });
  };

  // ФВ-5.19: попередження, які сервер попросив підтвердити; `null` — діалог закритий.
  const [warnings, setWarnings] = useState<string[] | null>(null);

  const submit = useMutation({
    mutationFn: (acknowledgeWarnings: boolean) =>
      apiFetch(`/api/v1/documents/${documentId}/submit`, {
        method: 'POST',
        body: JSON.stringify({
          sheetDefId,
          periodKey,
          acknowledgeWarnings,
        } satisfies SheetWorkflowRequest),
      }),
    onSuccess: async () => {
      setWarnings(null);
      await refresh();
      showDone(t('document.submitted'));
    },
    // ⚠ Причина показується як є: Submit при осиротілих рядках
    // (`ECR-SUB-4221`) — це не «помилка сервера», а перелік того, що треба
    // виправити. Виняток — «попередження без підтвердження» (ФВ-5.19): це
    // питання, а не відмова, тож замість тосту — діалог із переліком.
    onError: (error) => {
      const pending = warningsToConfirm(error);
      if (pending === null) {
        setWarnings(null);
        showSheetError(error, sheetName);
        return;
      }
      setWarnings(pending);
    },
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
    onError: (error) => showSheetError(error, sheetName),
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
    onError: (error) => showSheetError(error, sheetName),
  });

  /*
   * `BE-31`: відкликання подання автором. ⛔ Чи показати кнопку, вирішує
   * СЕРВЕР (`GET …/recall`): «автор подання» і «жоден крок не підписано» з
   * `/api/v1/me` і стану аркуша не виводяться. Запит іде лише на поданому
   * аркуші; доки відповіді немає — кнопки немає.
   */
  const recallAvailability = useRecallAvailability(
    documentId,
    sheetDefId,
    periodKey,
    isAllowed('recall', state),
  );
  const canRecall = isAllowed('recall', state) && recallAvailability.data?.canRecall === true;

  const recall = useMutation({
    mutationFn: (reason: string) =>
      apiFetch(`/api/v1/documents/${documentId}/recall`, {
        method: 'POST',
        body: JSON.stringify({ sheetDefId, periodKey, reason } satisfies RecallSheetRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setAsking(null);
      showDone(t('workflow.recalled'));
    },
    // ⚠ `409` тут — не збій: погоджувач устиг підписати крок, і текст каже саме це.
    onError: (error) => showSheetError(error, sheetName),
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
    onError: (error) => showSheetError(error, sheetName),
  });

  /**
   * ⛔ Захист від подвійного кліку — СИНХРОННИЙ, не через `isPending`.
   * Mantine `Button` вимикається лише разом із `loading`, а той оновлюється
   * лише на НАСТУПНОМУ рендері React — швидкий подвійний клік встигає
   * викликати `mutate()` двічі ДО першого перерендеру.
   *
   * ✎ AN-28 P2-2: раніше так був захищений лише Recalculate (`recalculateInFlight`);
   * Submit/Approve/Reject після L8-01 мали ще й вікно збереження набраного, у
   * якому кнопка не показувала зайнятості. Тепер усі дії аркуша — один
   * single-flight на весь шлях «зберегти -> дія -> відповідь».
   */
  const settled = useSettledAction(submit.isPending || decide.isPending || recalculate.isPending);

  const recalcJob = useQuery({
    queryKey: ['job', recalcJobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(recalcJobId ?? '')}`),
    enabled: recalcJobId !== null,

    // ⚠ Правило опитування — у чистому модулі `jobFollow.ts`: саме його не
    // було, і саме його треба перевіряти окремо від компонента.
    refetchInterval: (query) => pollInterval(query.state.data?.state, query.state.data?.effectiveState),

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
    : outcomeOf(recalcJob.data?.state, recalcJob.isError, recalcJob.data?.effectiveState);

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
      //
      // ⛔ `CL-02`: і саме зрізи ЦЬОГО аркуша, а не збіг за префіксом
      // `['table-slice']` — під нього підпадав кожен змонтований зріз (до 91
      // на аркуші), і всі вони йшли одночасно найважчим запитом системи.
      // Зрізи поза аркушем позначаються застарілими без запиту.
      void invalidateSlices(queryClient, {
        documentId: recalc.documentId,
        periodKey: recalc.periodKey,
        sheetDefId: recalc.sheetDefId,
      });

      void queryClient.invalidateQueries({
        queryKey: ['document', recalc.documentId, recalc.periodKey],
      });

      return;
    }

    // ⛔ Q-234: `error`, а не `message` — та сама плутанина полів, що й на
    // `PeriodsPage`/`ExportButton`. `Message` — останній прогрес
    // (`IJobProgress.ReportAsync`), на відмові він лишається тим, яким був
    // до неї; причину відмови несе `Error` (`FinishAsync`, `QuartzJobAdapter.cs`).
    // ⛔ `X-04`: тут ішов сирий `error` задачі — українське речення сервера чи
    // текст винятку SQL на англійському екрані. Людині — причина КОДОМ через
    // каталог (`JobFailure`: `err.<errorCode>`) і кореляція для підтримки.
    notifications.show({
      color: 'statusError',
      title: t('workflow.recalcFailed'),
      message: (
        <JobFailure
          state="Failed"
          errorCode={recalcJob.data?.errorCode}
          correlationId={recalcJob.data?.correlationId}
        />
      ),
    });
  }, [recalc, outcome, recalcJob.data?.errorCode, recalcJob.data?.correlationId, queryClient]);

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
  /**
   * ✎ D-285 (варіант B′, рішення координатора): сервер дозволяє подання з
   * рівнем >= Submit АБО >= Write разом із проєктним правом `Document.Submit`.
   * Клієнт цього права НЕ перевіряє: `/me` віддає лише глобальні права, тож
   * для ролі з областю проєкту його не видно. Тому «Submit» активна вже з
   * рівня `Write`, а відмову без права дає СЕРВЕР (403 `submitDenied`,
   * `deny.InsufficientGrantLevel.Submit`) — її показує `showSheetError` (з назвою аркуша, A2-08).
   */
  const mayWorkflow = (action: 'submit' | 'approve' | 'reject'): boolean =>
    me !== undefined &&
    !me.isSimulation &&
    meetsGrant(grant, action === 'submit' ? 'Write' : RequiredGrant[action]);

  // ⛔ `F-18`: архівний проєкт, закритий чи ще не відкритий період — подання й
  // перерахунок сервер однаково відхилить. Кнопка, яка гарантовано дасть
  // відмову, — це обіцянка, якої система не виконає; причину каже банер.
  const dataLocked = locksDataActions(lock);

  const canSubmit = !dataLocked && isAllowed('submit', state) && mayWorkflow('submit');

  /*
   * ⛔ A2-08 (F-25 / D-285): автор подання не погоджує власний аркуш — сервер
   * відповідає `403 approveOwnSubmission`. «Автора» клієнт сам не знає (`/me`
   * і стан аркуша не кажуть, хто подав), тож ознака — СЕРВЕРНА: `canRecall`
   * (`GET …/recall`) істинне лише для того, хто подав, і доки жоден крок не
   * підписано. Тоді замість Approve/Reject лишається «Recall» — штатний для
   * автора шлях забрати подання (Reject власного подання сервер пропускає, але
   * поруч із Recall це друга кнопка для тієї самої дії).
   * ⚠ Прогалина: якщо крок уже підписав інший погоджувач, `canRecall` хибне і
   * кнопка автору видна — тоді відмову з назвою аркуша дає сервер.
   */
  const isOwnSubmission = canRecall;

  const canApprove = !isOwnSubmission && isAllowed('approve', state) && mayWorkflow('approve');
  const canReject = !isOwnSubmission && isAllowed('reject', state) && mayWorkflow('reject');

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
  // ✎ 2026-10-02: перерахунок СВОГО документа — за читанням (сервер: `RecalculateDocumentHandler`),
  // а не за `Calculation.Recalculate` (те — проєктний/масовий перерахунок).
  // ✎ AN-39/L8-12: сервер (`RecalculateDocumentHandler`) відмовляє, коли ХОЧ ОДИН аркуш
  // періоду поданий чи затверджений, - кнопки, яка гарантовано дасть відмову, немає.
  const canRecalculate = !dataLocked && !hasLockedSheet(summary?.sheetStates) && can(me, 'Document.View');
  const recalcBusy = recalculate.isPending || recalcRunning || settled.settling;

  /*
   * ✎ `UI-41` (макет `screen-document.js`: F9 → `doRecalc`, «Recalculate» з `kbd: 'F9'`): F9
   * запускає РІВНО ту саму дію, що й кнопка, — `settled.run`, тобто спершу зберегти набране
   * (AN-28/L8-01). Кнопки немає (стан, права, блокування) — F9 нічого не робить.
   *
   * ⚠ Не з поля вводу, редактора комірки чи діалогу (`isTypingOrDialogTarget`): у редакторі
   * набір ще не зафіксовано (`keyCommitGate`), а дія за модальним вікном сталася б непомітно.
   * ⚠ Обробник ставиться ОДИН раз; актуальну дію він бере з `f9` (оновлюється після рендера).
   */
  const f9 = useRef<(() => void) | null>(null);
  useEffect(() => {
    f9.current = canRecalculate && !recalcBusy ? () => void settled.run(() => recalculate.mutateAsync()) : null;
  });
  useEffect(() => {
    const onKey = (event: KeyboardEvent): void => {
      if (!isRecalculateKey(event) || isTypingOrDialogTarget(event.target)) return;
      const run = f9.current;
      if (run === null) return;

      event.preventDefault();
      run();
    };

    window.addEventListener('keydown', onKey);

    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const hasAnyAction =
    canRecalculate ||
    canSubmit ||
    canApprove ||
    canReject ||
    canRecall ||
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
      {canRecalculate && (
        // ⚠ `Hint`, а не `Tooltip`: кнопка фокусується, але `Tooltip` не
        // давав `aria-describedby`, тож читач не чув нюансу про сусідні аркуші.
        <Hint label={t('workflow.recalculateHint')}>
          <Button
            variant="default"
            loading={recalcBusy}
            // AN-28/L8-01: спершу зберегти набране; відмова збереження - дії немає.
            onClick={() => settled.run(() => recalculate.mutateAsync())}
            // `UI-41`: читалка називає клавішу; на кнопці — видимий `F9`, як `kbd` у макеті.
            aria-keyshortcuts="F9"
            rightSection={
              <Text span size="xs" ff="monospace" c="dimmed" aria-hidden="true" data-recalc-kbd="">
                F9
              </Text>
            }
          >
            {recalcRunning ? t('workflow.recalcRunning') : t('workflow.recalculate')}
          </Button>
        </Hint>
      )}

      {canSubmit && (
        <Button loading={submit.isPending || settled.settling} onClick={() => settled.run(() => submit.mutateAsync(false))}>
          {t('document.submit')}
        </Button>
      )}

      {/* ⛔ Затвердження і відхилення — пара, і показуються разом. Кнопка
          «Затвердити» без «Відхилити» перетворює погодження на формальність:
          єдиний спосіб не затвердити — не натиснути нічого, і аркуш висить
          у `Submitted` без жодного сліду причини. */}
      {/* ⛔ `X-05`: `color="green"` давав білий текст на заливці з
          контрастом 2.4:1 — токен `statusSuccess` підібрано під `primaryShade`
          цієї теми до AA 4.5:1 (`theme.ts`, `contrast.test.ts`).
          ⛔ `X-25`: затвердження йшло одним кліком без підтвердження, хоча
          «Reject» і «Return for edits» питають. Затверджені дані йдуть у
          звітність регулятору — це та сама вага рішення. */}
      {canApprove && (
        <Button
          color="statusSuccess"
          loading={decide.isPending || settled.settling}
          onClick={() => setAsking('approve')}
        >
          {t('workflow.approve')}
        </Button>
      )}

      {/* ✎ 2026-10-06: у панелі дій — лише варіанти з рамкою чи заливкою
          (`docs/design/ui-conventions.md`, «Кнопки»): `light` без рамки
          поруч з обвідними читався як кнопка іншого розміру. */}
      {canReject && (
        <Button color="statusError" variant="outline" onClick={() => setAsking('reject')}>
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
        <Button variant="default" onClick={() => setAsking('reopen')}>
          {t('workflow.reopen')}
        </Button>
      )}

      {canRecall && (
        <Button variant="default" onClick={() => setAsking('recall')}>
          {t('workflow.recall')}
        </Button>
      )}

      <LazyConfirmModal
        opened={warnings !== null}
        title={t('workflow.submitWarningsTitle')}
        text={t('workflow.submitWarningsHint')}
        consequences={warnings ?? []}
        verb={t('workflow.submitAnyway')}
        danger={false}
        isPending={submit.isPending || settled.settling}
        onConfirm={() => settled.run(() => submit.mutateAsync(true))}
        onClose={() => setWarnings(null)}
      />

      <LazyConfirmModal
        opened={asking === 'approve'}
        title={t('workflow.approveTitle')}
        text={t('workflow.approveHint')}
        verb={t('workflow.approve')}
        danger={false}
        isPending={decide.isPending || settled.settling}
        onConfirm={() => settled.run(() => decide.mutateAsync({ approved: true, reason: null }))}
        onClose={() => setAsking(null)}
      />

      <LazyReasonModal
        opened={asking === 'recall'}
        title={t('workflow.recallTitle')}
        label={t('workflow.reason')}
        description={t('workflow.recallHint')}
        confirmLabel={t('workflow.recall')}
        isPending={recall.isPending}
        onConfirm={(reason) => recall.mutate(reason)}
        onClose={() => setAsking(null)}
      />

      <LazyReasonModal
        opened={asking === 'reject'}
        title={t('workflow.rejectTitle')}
        label={t('workflow.reason')}
        description={t('workflow.rejectHint')}
        confirmLabel={t('workflow.reject')}
        isPending={decide.isPending || settled.settling}
        onConfirm={(reason) => settled.run(() => decide.mutateAsync({ approved: false, reason }))}
        onClose={() => setAsking(null)}
      />

      <LazyReasonModal
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

/**
 * Тексти попереджень із відмови «потрібне підтвердження» (ФВ-5.19):
 * `422 ECR-SUB-4221`, `messageKey = err.ECR-SUB-4221.warningsNeedConfirmation`,
 * перелік у `messages[].message` (локалізований сервером). `null` — це інша відмова.
 */
export function warningsToConfirm(error: unknown): string[] | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== 'ECR-SUB-4221') return null;

  const extensions = error.problem.extensions2;
  if (extensions?.['messageKey'] !== 'err.ECR-SUB-4221.warningsNeedConfirmation') return null;

  const messages = extensions['messages'];
  if (!Array.isArray(messages)) return [];

  return messages.map((m: { message?: unknown }) => String(m.message ?? ''));
}

/** Чи має аркуш у цьому стані бути доступним для правки. */
export function isEditable(state: string): boolean {
  // Подане і затверджене не редагується: щоб змінити числа, аркуш повертають
  // у роботу окремою дією з причиною (`ФВ-5.20a`).
  return !isAllowed('approve', state) && !isAllowed('reopen', state);
}

export type { WorkflowAction };
