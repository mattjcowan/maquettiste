import { defineConfig, mergeConfig } from "vitest/config";
import viteConfig from "./vite.config";

export default defineConfig((env) =>
  mergeConfig(viteConfig({ ...env, mode: "test" }), {
    test: {
      environment: "jsdom",
      setupFiles: ["tests/setup.ts"],
      include: ["tests/unit/**/*.test.{ts,tsx}"],
      testTimeout: 20000,
      css: false,
    },
  }),
);
