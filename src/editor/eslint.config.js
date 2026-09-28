// ESLint 9 flat config: typescript-eslint, the React hooks rules, and the design rule that no color
// literal lives outside src/design/tokens.css (scripts/check-hex.mjs covers CSS files too).
import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

const HEX = "^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$";

export default tseslint.config(
  {
    ignores: [
      "dist",
      "dist-mock",
      "node_modules",
      "src/api/schema.d.ts",
      "src/mocks/fixture",
      "src/mocks/recorded",
      "public",
      "test-results",
      "playwright-report",
    ],
  },
  {
    files: ["**/*.{ts,tsx}"],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: { ecmaVersion: 2023, globals: { ...globals.browser, ...globals.node } },
    plugins: { "react-hooks": reactHooks },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "error",
      "@typescript-eslint/no-unused-vars": ["error", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
      "no-restricted-syntax": [
        "error",
        { selector: `Literal[value=/${HEX}/]`, message: "Colors come from tokens.css (var(--mq-…)), never a hex literal." },
        { selector: `TemplateElement[value.raw=/#[0-9a-fA-F]{6}\\b/]`, message: "Colors come from tokens.css (var(--mq-…)), never a hex literal." },
      ],
    },
  },
  {
    // The contrast test pins the adjusted token values themselves.
    files: ["tests/unit/tokens.test.ts", "tests/unit/color.test.ts"],
    rules: { "no-restricted-syntax": "off" },
  },
  {
    files: ["scripts/**/*.mjs", "*.config.{js,ts}"],
    extends: [js.configs.recommended],
    languageOptions: { globals: globals.node },
  },
);
