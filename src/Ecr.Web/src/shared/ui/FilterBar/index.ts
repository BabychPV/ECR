/**
 * Панель фільтрів (`KIT.md` §6.3): рядок фільтрів з адресою (`FilterBar`) і
 * спільна будова ряду, якою користуються всі панелі фільтрів (`FilterRow`,
 * `FilterInline`). Точка входу одна — цей файл.
 */
export { FilterBar, type FilterOption, type FilterSpec } from './FilterBar';
export { FilterInline, FilterRow, type FilterRowProps } from './FilterRow';
export { FilterHints, readerOnlyDescription } from './FilterHints';
