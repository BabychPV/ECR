import { apiFetch, apiFetchResponse } from '@/api/client';
import type { components } from '@/api/schema';
import type { LanguageDto } from '@/api/types';
import { fileNameOf } from '@/features/audit/api';
import { DefaultLanguage, t } from '@/shared/i18n';

/**
 * Обмін перекладом інтерфейсу через CSV (`BE-13` ч.2).
 *
 * ⛔ Обидві дії сервера — `GET /ui-strings/export.csv` і
 * `POST /ui-strings/import` — не мали в клієнті жодного споживача, і сторож
 * `EndpointCoverageTests.Кожна_дія_сервера_має_споживача_в_інтерфейсі` на цій
 * гілці червоний саме через них. Це не формальність: без екрана переклад
 * тисячі рядків лишається роботою по одному полю в таблиці, а вивантажити їх
 * термінологові в Excel неможливо взагалі.
 */

/** Звіт імпорту — форма **згенерована** зі знімка OpenAPI, не описана тут. */
export type UiStringImportReport = components['schemas']['UiStringImportReport'];

/** Одна відхилена стрічка файлу. */
export type UiStringImportError = components['schemas']['UiStringImportError'];

/** Ім'я файлу, коли сервер не надіслав `Content-Disposition`. */
export const FallbackExportFileName = 'ui-strings.csv';

/** Завантажений CSV: тіло й ім'я, яке запропонував сервер (або `null`). */
export interface UiStringExportFile {
  readonly blob: Blob;
  readonly fileName: string | null;
}

/**
 * CSV перекладу однієї мови (`key, scope, en, <lang>, updatedAt`).
 *
 * ⛔ `fetch` → blob, а не посилання: за посиланням браузер показав би відмову
 * (`422` на мову за замовчуванням, `403` без права) сирим JSON на порожній
 * сторінці. Той самий аргумент і той самий шлях, що в експорті аудиту
 * (`features/audit/api.ts`, `BE-16`).
 *
 * ⚠ `method: 'GET'` названо явно: сторож `EndpointCoverageTests` виводить
 * метод із назви функції, а `apiFetchResponse` у його переліку немає.
 */
export async function fetchUiStringExport(lang: string): Promise<UiStringExportFile> {
  const response = await apiFetchResponse(
    `/api/v1/ui-strings/export.csv?lang=${encodeURIComponent(lang)}`,
    { method: 'GET' },
  );

  return {
    blob: await response.blob(),
    fileName: fileNameOf(response.headers.get('Content-Disposition')),
  };
}

/** Що саме надсилаємо на перевірку або на запис. */
export interface UiStringImportInput {
  /** Мова перекладу; мова за замовчуванням сервером не приймається. */
  readonly lang: string;

  /** Той самий об'єкт `File`, який людина обрала. */
  readonly file: File;

  /** `true` — лише звіт, нічого не записується. */
  readonly dryRun: boolean;
}

/**
 * Імпорт перекладу з CSV.
 *
 * ⛔ `dryRun` — ОБОВ'ЯЗКОВИЙ параметр, а не необов'язковий із дефолтом.
 * Дефолт `false` означав би, що пропущений аргумент мовчки пише в базу — тобто
 * найнебезпечніший виклик був би найкоротшим.
 *
 * ⚠ Ім'я поля — `file`: саме так називається параметр `IFormFile file` у
 * контролері (той самий урок, що в `features/import/ImportPanel.tsx`).
 * `Content-Type` не задається: межу multipart генерує браузер.
 */
export function importUiStrings(input: UiStringImportInput): Promise<UiStringImportReport> {
  const form = new FormData();
  form.append('file', input.file);

  return apiFetch<UiStringImportReport>(
    `/api/v1/ui-strings/import?lang=${encodeURIComponent(input.lang)}&dryRun=${String(input.dryRun)}`,
    { method: 'POST', body: form },
  );
}

/**
 * Мови, придатні як ціль обміну: усе, крім мови-еталона.
 *
 * ⛔ Не «крім `en`» жорстко в коді: еталон називає САМ реєстр
 * (`LanguageDto.isDefault`), і вимога «додавання мови — запис у реєстр, не
 * збірка клієнта» діє й тут. `DefaultLanguage` лишається другою умовою лише як
 * запобіжник на випадок відповіді без `isDefault`.
 *
 * ⛔ Сервер відмовляє еталону однаково в ЕКСПОРТІ й в імпорті
 * (`RequireTranslationLanguageAsync`, `err.ECR-REQ-0422.uiStringCsvLanguage`):
 * `en` — мірило плейсхолдерів, а не переклад. Тому його немає в переліку обох
 * дій, а не лише імпорту: пропонувати кнопку, яка завжди дає 422, — це
 * показувати відмову замість того, щоб її не допустити.
 */
export function translationLanguages(languages: readonly LanguageDto[] | undefined): LanguageDto[] {
  return (languages ?? []).filter(
    (language) => !language.isDefault && language.code !== DefaultLanguage,
  );
}

/**
 * Текст відмови однієї стрічки — з каталогу, не сирий ключ.
 *
 * ⛔ `t(error.messageKey)` одним динамічним викликом виглядав би коротше і був
 * би ГІРШИМ: сторож
 * `EndpointCoverageTests.Кожен_рядок_якого_просить_клієнт_є_в_каталозі`
 * перевіряє лише ключі-літерали, тож жоден із цих п'яти рядків не був би нічим
 * підтверджений — прогалина в сіді доїхала б до людини як `⟦…⟧` у таблиці
 * помилок. Перелік літералів робить кожен із них перевіреним.
 *
 * ⚠ Запасний варіант — загальний рядок коду `ECR-REQ-0422`, а не сам ключ:
 * шостий `messageKey`, доданий на сервері без рядка тут, має дати людині
 * речення, а не технічну назву.
 *
 * ⚠ Чотири власні рядки `BE-13` ч.2 підстановок не мають навмисно («відмови
 * рядків приходять у звіті без підстановок», коментар у сіді). П'ятий,
 * `placeholderMismatch`, узятий із редактора рядків і чекає на `{key}`,
 * `{expected}`, `{actual}`; у звіті є лише `key`, і він підставляється. Двох
 * інших сервер у `UiStringImportError` не надсилає взагалі — вони лишаються в
 * тексті як є, і це видима прогалина СЕРВЕРА, а не місце, де можна вигадати
 * значення: неправдиві «очікувалось [a], отримано [b]» коштували б дорожче за
 * порожні дужки.
 *
 * @param messageKey Ключ відмови, як його назвав сервер.
 * @param key Ключ каталогу з цієї стрічки файлу.
 */
export function rowErrorText(messageKey: string, key: string): string {
  switch (messageKey) {
    case 'err.ECR-REQ-0422.uiStringUnknownKey':
      return t('err.ECR-REQ-0422.uiStringUnknownKey', { key });
    case 'err.ECR-REQ-0422.uiStringEmptyValue':
      return t('err.ECR-REQ-0422.uiStringEmptyValue', { key });
    case 'err.ECR-REQ-0422.uiStringTooLong':
      return t('err.ECR-REQ-0422.uiStringTooLong', { key });
    case 'err.ECR-REQ-0422.uiStringDuplicateKey':
      return t('err.ECR-REQ-0422.uiStringDuplicateKey', { key });
    case 'err.ECR-REQ-0422.placeholderMismatch':
      return t('err.ECR-REQ-0422.placeholderMismatch', { key });
    default:
      return t('err.ECR-REQ-0422', { key });
  }
}

/**
 * Чи звіт дозволяє запис.
 *
 * ⛔ Умова саме «помилок немає», а не «є що записати»: сервер працює за
 * правилом «все або нічого» (`UiStringCsvHandlers`), і одна відхилена стрічка
 * означає, що з файлу не поїде НІЧОГО. Кнопка «Застосувати», доступна при
 * непорожньому `errors`, обіцяла б часткове застосування, якого не існує.
 */
export function isApplicable(report: UiStringImportReport | null): boolean {
  return report !== null && report.errors.length === 0;
}
