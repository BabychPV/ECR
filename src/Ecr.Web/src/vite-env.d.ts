/// <reference types="vite/client" />

/*
 * ⚠ Файла не було, і без нього `import.meta.env` не існує для компілятора:
 * `tsc` падає на `Property 'env' does not exist on type 'ImportMeta'`.
 * Vite підставляє ці значення при збірці, а типи до них приходять саме звідси.
 */
