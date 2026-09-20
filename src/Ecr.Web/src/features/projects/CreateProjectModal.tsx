import { useState, type JSX } from 'react';
import { Button, Group, Modal, NumberInput, Select, Text, TextInput } from '@mantine/core';
import { useMutation, useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  CreateProjectRequest,
  PeriodPolicyDto,
  ProjectIdResponse,
  TemplatePage,
  TemplateVersionPage,
} from '@/api/types';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Види періоду; значення збігаються з `PeriodKind` домену. */
const PeriodKinds = ['Monthly', 'Quarterly', 'Yearly', 'Custom'];

/**
 * Перелік поясів IANA.
 *
 * ⛔ `supportedValuesOf('timeZone')` віддає САМЕ ідентифікатори IANA
 * (`Asia/Aqtau`) — інших сервер не приймає (`ECR-CFG-4221`, директива ПК-1
 * №06 §3). Вільного введення тут немає навмисно: пояс вічний (`ФВ-1.1a`), і
 * опечатка в ньому стала б вічною властивістю проєкту.
 *
 * ⚠ `supportedValuesOf` є не всюди (і немає в старих середовищах). Без
 * запасного варіанта поле лишалося б порожнім, а проєкт — нествореним: сервер
 * відхиляє створення без поясу.
 */
export function timeZones(): string[] {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const all = typeof supported === 'function' ? supported('timeZone') : [];

  const fallback = ['Asia/Almaty', 'Asia/Aqtau', 'Asia/Atyrau', 'Asia/Oral', 'Europe/London', 'UTC'];

  return all.length > 0 ? all : fallback;
}

/**
 * Тіло запиту на створення проєкту.
 *
 * ⛔ Винесене з компонента ОКРЕМОЮ функцією навмисно. Дефект `A7-56` жив
 * саме тут: у тілі не було ані `templateVersionId`, ані `periodPolicyId`, і
 * сервер відхиляв кожну спробу (`ECR-TMPL-0404`). Перевірити це рендером
 * Mantine у jsdom можна, але один такий тест іде понад дві хвилини — тобто
 * його вимкнуть. Чиста функція перевіряється за мілісекунди й пильнує рівно
 * те, що ламалося: **склад тіла**.
 *
 * ⚠ Числа перетворюються ТУТ. `Select` віддає рядок, а сервер чекає `int`:
 * `"42"` у полі `templateVersionId` дало б `400` ще до обробника.
 */
export function createProjectBody(form: {
  code: string;
  name: LocalizedValue;
  periodKind: string;
  timeZoneId: string;
  versionId: string | null;
  policyId: string | null;
  customPeriodCount?: number | null;
}): CreateProjectRequest {
  return {
    code: form.code.trim(),
    nameL10n: form.name,
    periodKind: form.periodKind,

    // ⚠ Часовий пояс — ПРОЄКТУ, а не сервера і не браузера: межі періоду
    // рахуються в ньому (`D-6`). Сервер у Європі не має вирішувати, коли
    // закінчився місяць на місці видобутку — і браузер конфігуратора теж.
    timeZoneId: form.timeZoneId,
    templateVersionId: Number(form.versionId),
    periodPolicyId: Number(form.policyId),

    // ⛔ T6/#36: надсилається лише для `Custom` — для решти періодичностей
    // кількість визначає сам вид, і поле лишається `null`, а не нулем чи
    // старим значенням із попереднього вибору `Custom` у тій самій формі.
    customPeriodCount: form.periodKind === 'Custom' ? (form.customPeriodCount ?? null) : null,
  };
}

/** Поле форми створення проєкту, якого може бракувати для надсилання. */
type MissingProjectField = 'code' | 'name' | 'timeZone' | 'version' | 'policy' | 'customPeriodCount';

/** Ключ напису для кожного бракуючого поля — той самий, що й у `label` полів нижче. */
const ProjectFieldLabelKey: Record<MissingProjectField, string> = {
  code: 'periods.code',
  name: 'periods.name',
  timeZone: 'periods.timeZone',
  version: 'periods.templateVersion',
  policy: 'periods.policy',
  customPeriodCount: 'periods.customCount',
};

/**
 * Чого формі бракує, щоб її можна було надіслати.
 *
 * ⛔ Винесене ОКРЕМОЮ чистою функцією з тієї ж причини, що й
 * {@link createProjectBody} (`D1-12`): рендер Mantine у jsdom для цієї форми
 * йде понад дві хвилини, тобто такий тест вимкнули б. Перевіряти ж тут є що —
 * пояс став обов'язковим, і саме забутий пункт у цьому переліку дозволив би
 * надіслати форму без нього.
 *
 * ⚠ `timeZoneId === null` означає «не обрано». Порожній рядок сюди не
 * потрапляє: `Select` віддає або значення зі списку, або `null`.
 *
 * ⛔ Q-298: раніше існував лише `boolean` (`createProjectIncomplete` нижче) —
 * кнопка ставала `disabled` без жодного пояснення, ЯКЕ саме поле ще
 * порожнє. Перелік бракуючих полів рахується тут же, чистою функцією, без
 * рендеру Mantine в jsdom — той самий підхід, яким варто було б колись
 * замінити й голий `disabled={...}` у `CreateDocumentModal.tsx`.
 */
export function createProjectMissingFields(form: {
  code: string;
  name: LocalizedValue;
  timeZoneId: string | null;
  versionId: string | null;
  policyId: string | null;
  periodKind?: string;
  customPeriodCount?: number | null;
}): MissingProjectField[] {
  const missing: MissingProjectField[] = [];

  if (form.code.trim().length === 0) missing.push('code');
  if (!hasAnyText(form.name)) missing.push('name');
  if (form.timeZoneId === null) missing.push('timeZone');
  if (form.versionId === null) missing.push('version');
  if (form.policyId === null) missing.push('policy');

  // ⛔ T6/#36: `Custom` без кількості надсилає `customPeriodCount: null`,
  // і сервер відхиляє це як `0` поза межами `1..12` (`ECR-PRD-4224`) —
  // краще не давати надіслати запит, який гарантовано відмовлять.
  if (form.periodKind === 'Custom' && !(form.customPeriodCount && form.customPeriodCount > 0)) {
    missing.push('customPeriodCount');
  }

  return missing;
}

/**
 * Чи готова форма до надсилання.
 *
 * ⚠ Тонка обгортка над {@link createProjectMissingFields}: наявні виклики й
 * тести очікують `boolean`, а компонент додатково показує САМ перелік
 * (`missingFields` нижче) як підказку біля кнопки — дві різні обіцянки з
 * однієї перевірки, а не дублювання логіки.
 */
export function createProjectIncomplete(form: {
  code: string;
  name: LocalizedValue;
  timeZoneId: string | null;
  versionId: string | null;
  policyId: string | null;
  periodKind?: string;
  customPeriodCount?: number | null;
}): boolean {
  return createProjectMissingFields(form).length > 0;
}

/**
 * Створення проєкту (`ФВ-1.1`, `D-5`).
 *
 * ⛔ Форма винесена зі сторінки не заради стислості. `A7-56`: вона надсилала
 * запит **без версії шаблону і без політики періодів**, а сервер відхиляє
 * створення без них (`ECR-TMPL-0404`, `ECR-PRD-0422`). Тобто перший крок
 * роботи із системою не працював із інтерфейсу жодного разу, а сторож «кожна
 * дія сервера має споживача» був зелений: споживач був, просто ніколи не
 * спрацьовував. Окремий компонент дає цій формі власний тест, який дивиться
 * на **тіло запиту**, а не на те, що форма відкрилася.
 *
 * ⚠ Проєкт створюється ЧЕРНЕТКОЮ: періоди лишаються закритими, доки його не
 * активують. Це не зайвий крок — календар будується з рішень, які доти ще
 * можна виправити без сліду в аудиті.
 */
export function CreateProjectModal({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (projectId: number) => void | Promise<void>;
}): JSX.Element {
  const [code, setCode] = useState('');
  const [name, setName] = useState<LocalizedValue>({});
  const [periodKind, setPeriodKind] = useState('Monthly');

  // ⛔ Поле починається ПОРОЖНІМ (директива ПК-1 №06 §3 скасувала `D1-09`).
  // Тут стояв пояс браузера як початкове значення — і це та сама мовчазна
  // підстановка, тільки на крок пізніше: форму можна було надіслати, жодного
  // разу не глянувши на поле, і пояс конфігуратора ставав ВІЧНОЮ властивістю
  // проєкту (`ФВ-1.1a`). Конфігуратор сидить в Астані, майданчик — в Актау,
  // це +06:00 проти +05:00: кожен період закривався б на годину раніше, ніж
  // чекають на місці, і помітили б це за скаргою «не встиг подати».
  const [timeZoneId, setTimeZoneId] = useState<string | null>(null);
  const [versionId, setVersionId] = useState<string | null>(null);
  const [policyId, setPolicyId] = useState<string | null>(null);

  // T6/#36: скільки періодів у Custom-проєкті; сенс має лише при
  // `periodKind === 'Custom'` — для решти вид сам визначає кількість.
  const [customPeriodCount, setCustomPeriodCount] = useState<number | null>(null);

  const templates = useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
    enabled: opened,
  });

  // ⛔ Q-274: `GET /api/v1/templates/{id}/versions` — курсорний ендпоінт
  // (Q-225), відповідь `{items, nextCursor, totalCount}`, а не голий масив.
  // Тут стояв тип `TemplateVersionSummary[]`, тож `.data` при пагінованій
  // відповіді був ОБ'ЄКТОМ, не `undefined` — `?? []` не рятував, і
  // `.filter` нижче падав на кожному відкритті цієї форми (`ECR-WEB-8662`).
  // `TemplatesPage.tsx` уже читає той самий ендпоінт правильно
  // (`TemplateVersionPage`, `?limit=100`, `.data?.items`) — той самий взірець.
  const versionQueries = useQueries({
    queries: (templates.data?.items ?? []).map((template) => ({
      queryKey: queryKeys.templates.versionsOf(template.id),
      queryFn: () =>
        apiFetch<TemplateVersionPage>(`/api/v1/templates/${template.id}/versions?limit=100`),
      enabled: opened,
    })),
  });

  const policies = useQuery({
    queryKey: ['period-policies'],
    queryFn: () => apiFetch<PeriodPolicyDto[]>('/api/v1/projects/period-policies'),
    enabled: opened,
  });

  /** Опубліковані версії всіх шаблонів, підписані кодом шаблону. */
  const publishedVersions = (templates.data?.items ?? []).flatMap((template, index) =>
    (versionQueries[index]?.data?.items ?? [])
      .filter((version) => version.status === 'Published')
      .map((version) => ({
        value: String(version.id),
        label: `${template.code} · ${version.version}`,
      })),
  );

  const create = useMutation({
    mutationFn: () =>
      apiFetch<ProjectIdResponse>('/api/v1/projects', {
        method: 'POST',
        body: JSON.stringify(
          // `timeZoneId` тут уже не `null`: кнопка недоступна, доки пояс не
          // обрано (`incomplete` нижче).
          createProjectBody({
            code, name, periodKind, timeZoneId: timeZoneId ?? '', versionId, policyId, customPeriodCount,
          }),
        ),
      }),
    onSuccess: async (result) => {
      setCode('');
      setName({});
      setTimeZoneId(null);
      setVersionId(null);
      setPolicyId(null);
      setCustomPeriodCount(null);
      onClose();
      await onCreated(result.projectId);
    },
    onError: showApiError,
  });

  // ⛔ Пояс у переліку обов'язкових. Без нього форму можна було надіслати
  // (браузерне значення підставлялося саме), і сервер приймав її — з чужим
  // поясом, який після відкриття першого періоду вже не змінити (`ФВ-1.1a`).
  //
  // ⛔ Q-298: раніше тут одразу рахувався `boolean`, і кнопка ставала
  // `disabled` без жодного пояснення, чого саме бракує (той самий пробіл
  // UX, що й `CreateDocumentModal.tsx` до свого фіксу). Перелік бракуючих
  // полів рахується один раз і використовується двічі: для `incomplete` і
  // для підказки біля кнопки нижче.
  const missingFields = createProjectMissingFields({
    code, name, timeZoneId, versionId, policyId, periodKind, customPeriodCount,
  });
  const incomplete = missingFields.length > 0;

  /*
   * ⛔ Обидва обов'язкові переліки — версія шаблону й політика періодів —
   * збиралися через `?? []`, тобто при відмові сервера ставали ПОРОЖНІМИ і
   * мовчали. А підказка нижче (`periods.stillNeeded`) сумлінно перелічувала їх
   * як «ще не заповнено».
   *
   * ⚠ Наслідок не «людина не зрозуміла»: конфігуратор читає порожній перелік
   * як «опублікованих версій шаблону ще немає» чи «політик не заведено» — і
   * йде створювати ЩЕ ОДНУ версію шаблону або ЩЕ ОДНУ політику. Відмова запиту
   * штовхає його робити зайву, а потім дублюючу конфігурацію, яку хтось
   * прибиратиме.
   *
   * ⚠ Версії живуть у `useQueries` — по запиту на шаблон; береться ПЕРША
   * відмова з усіх трьох джерел. Три банери поруч розповідали б про
   * влаштування клієнта замість того, що сталося.
   */
  const loadError =
    templates.error ??
    policies.error ??
    versionQueries.find((query) => query.error !== null)?.error ??
    null;

  const refetchSources = (): void => {
    void templates.refetch();
    void policies.refetch();
    versionQueries.forEach((query) => void query.refetch());
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('periods.create')}>
      {/*
       * ⛔ Перед полями, а не після: людина має побачити причину ДО того, як
       * почне гадати, чому переліки порожні. Решта форми лишається робочою —
       * код, назву й пояс заповнити можна, а кнопка й так заблокована, бо
       * обов'язкові поля не обрані.
       */}
      {loadError !== null && <ErrorAlert error={loadError} onRetry={refetchSources} />}

      <TextInput
        label={t('periods.code')}
        description={t('periods.codeHint')}
        value={code}
        onChange={(event) => setCode(event.currentTarget.value)}
        data-autofocus
      />

      <LocalizedInput label={t('periods.name')} value={name} onChange={setName} />

      {/* ⛔ Вид періоду задається при створенні і потім визначає весь
          календар: у квартальному проєкті `Sequence` іде від 1 до 4, і
          змінити це згодом означало б переписати ключі всіх даних. */}
      <Select
        mt="sm"
        label={t('periods.kind')}
        description={t('periods.kindHint')}
        data={PeriodKinds}
        value={periodKind}
        onChange={(value) => setPeriodKind(value ?? 'Monthly')}
        allowDeselect={false}
      />

      {/* ⛔ T6/#36: без цього поля `PeriodKind.Custom` був недосяжний через
          API — домен розумів його, а форма не мала звідки взяти кількість
          періодів, тож запит завжди йшов би з нею відсутньою. Видиме лише
          для `Custom`: для решти видів кількість визначає сам вид. */}
      {periodKind === 'Custom' && (
        <NumberInput
          mt="sm"
          required
          label={t('periods.customCount')}
          description={t('periods.customCountHint')}
          min={1}
          max={12}
          value={customPeriodCount ?? ''}
          onChange={(value) => setCustomPeriodCount(typeof value === 'number' ? value : null)}
        />
      )}

      {/* ⛔ Пояс ВИДИМИЙ, ОБОВ'ЯЗКОВИЙ і без початкового значення (директива
          ПК-1 №06 §3). Спершу він надсилався мовчки з браузера, потім браузер
          лише підставляв початкове значення — обидва варіанти дозволяли
          створити проєкт, жодного разу не подивившись на поле. Помилку тут не
          виправити після відкриття першого періоду (`ФВ-1.1a`). */}
      <Select
        mt="sm"
        required
        searchable
        // ⚠ Поясів близько шестисот. Без межі список малюється весь: пошук
        // лишається по ВСЬОМУ переліку, обрізається лише показане.
        limit={50}
        label={t('periods.timeZone')}
        description={t('periods.timeZoneHint')}
        data={timeZones()}
        value={timeZoneId}
        onChange={setTimeZoneId}
      />

      {/* ⚠ Лише ОПУБЛІКОВАНІ версії: чернетка не має ані замороженої
          структури, ані гарантії, що комірки знайдуть свої описи. */}
      <Select
        mt="sm"
        label={t('periods.templateVersion')}
        description={t('periods.templateVersionHint')}
        data={publishedVersions}
        value={versionId}
        onChange={setVersionId}
      />

      <Select
        mt="sm"
        label={t('periods.policy')}
        description={t('periods.policyHint')}
        data={(policies.data ?? []).map((policy) => ({
          value: String(policy.id),
          label: `${policy.code} · +${policy.graceOffsetDays}/${policy.hardCloseOffsetDays}`,
        }))}
        value={policyId}
        onChange={setPolicyId}
      />

      {/* ⛔ Q-298: раніше кнопка була просто `disabled` без жодного
          пояснення, чого саме бракує: користувач бачив непрацездатну кнопку
          і мав сам здогадатися, яке поле ще заповнити. `CreateDocumentModal.tsx`
          має той самий дефект (`disabled={...}` без підказки) — цей фікс
          його поки не зачіпає, лише документує ту саму форму рішення на
          майбутнє. */}
      {incomplete && (
        <Text size="xs" c="dimmed" mt="sm">
          {t('periods.stillNeeded', {
            fields: missingFields.map((field) => t(ProjectFieldLabelKey[field])).join(', '),
          })}
        </Text>
      )}

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button disabled={incomplete} loading={create.isPending} onClick={() => create.mutate()}>
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
