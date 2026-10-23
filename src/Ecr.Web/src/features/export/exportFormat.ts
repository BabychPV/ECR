import { t } from '@/shared/i18n';

/*
 * ⚠ Окремим модулем від `ExportButton.tsx` (`D-132`): пунктам меню «More» сторінки документа потрібні
 * лише формати й підписи, а сама кнопка (задача експорту, опитування, тост) вантажиться з першим
 * запуском експорту. `ExportButton.tsx` реекспортує обидва.
 */

/**
 * Формат вивантаження (ФВ-4.2). Ті самі три значення, що приймає сервер
 * (`POST …/export` тіло `ExportRequest.format`); четвертого немає й додавати
 * нема звідки — перелік валідних значень визначає СЕРВЕР (422
 * `err.ECR-REQ-0422.exportFormatUnknown` на будь-яке інше), тому клієнт не
 * дублює цю перевірку — він лише не дає обрати нічого поза цими трьома.
 */
export type ExportFormat = 'xlsx' | 'csv' | 'json';

/**
 * Підписи перемикача формату — самі значення, а не переклад лейблів, беруться
 * з каталогу (`t()`), щоб не заводити четвертий літерал в UI поруч із трьома
 * дозволеними значеннями контракту.
 */
export function exportFormatOptions(): { value: ExportFormat; label: string }[] {
  return [
    { value: 'xlsx', label: t('document.exportFormatXlsx') },
    { value: 'csv', label: t('document.exportFormatCsv') },
    { value: 'json', label: t('document.exportFormatJson') },
  ];
}
