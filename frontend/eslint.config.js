import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist'] },
  {
    extends: [
      js.configs.recommended,
      ...tseslint.configs.recommended,
      reactHooks.configs['recommended-latest'],
      jsxA11y.flatConfigs.recommended,
    ],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      'react-refresh': reactRefresh,
    },
    rules: {
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      // Catches exactly the import-hygiene problems flagged in review: an import left behind
      // after the code using it is removed, and an unused local.
      '@typescript-eslint/no-unused-vars': ['warn', { argsIgnorePattern: '^_' }],
      // A test file legitimately imports a type only used in an annotation; the base rule is
      // strict enough elsewhere without also flagging that.
      '@typescript-eslint/consistent-type-imports': ['warn', { prefer: 'type-imports' }],
      // Default depth (2) doesn't reach text wrapped in a styling <div> plus an inline <code>/
      // <span> — a real, common layout, not a missing label. Raised rather than flattening
      // working markup to satisfy the linter's default.
      'jsx-a11y/label-has-associated-control': ['error', { depth: 4 }],
    },
  },
);
