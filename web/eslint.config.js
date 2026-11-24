import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import pluginVue from 'eslint-plugin-vue';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import vueParser from 'vue-eslint-parser';

const typeAwareParserOptions = {
  project: ['./tsconfig.json'],
  tsconfigRootDir: import.meta.dirname,
  extraFileExtensions: ['.vue'],
};

export default [
  {
    ignores: [
      'dist/**',
      'coverage/**',
      'node_modules/**',
      'test-results/**',
      'playwright-report/**',
      // Copied as it is from bee-san/dictionary-popup; src/vendor/dictionary-popup/SOURCES.md.
      'src/vendor/**',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommendedTypeChecked,
  ...pluginVue.configs['flat/recommended'],
  {
    files: ['**/*.{ts,vue}'],
    languageOptions: {
      globals: globals.browser,
      parserOptions: typeAwareParserOptions,
    },
    rules: {
      '@typescript-eslint/consistent-type-imports': ['error', { fixStyle: 'inline-type-imports' }],
      '@typescript-eslint/no-misused-promises': [
        'error',
        { checksVoidReturn: { attributes: false } },
      ],
      'vue/multi-word-component-names': 'off',
      'vue/no-v-html': 'error',
    },
  },
  {
    files: ['**/*.vue'],
    languageOptions: {
      parser: vueParser,
      parserOptions: {
        ...typeAwareParserOptions,
        parser: tseslint.parser,
      },
    },
  },
  {
    // The service worker has the worker's globals and is typed against them.
    files: ['src/worker/sw.ts'],
    languageOptions: {
      globals: globals.serviceworker,
      parserOptions: { ...typeAwareParserOptions, project: ['./tsconfig.worker.json'] },
    },
  },
  {
    files: ['e2e/**/*.ts', '*.config.ts'],
    languageOptions: {
      globals: globals.node,
    },
  },
  {
    files: ['*.config.js'],
    ...tseslint.configs.disableTypeChecked,
    languageOptions: {
      ...tseslint.configs.disableTypeChecked.languageOptions,
      globals: globals.node,
    },
  },
  prettier,
];
