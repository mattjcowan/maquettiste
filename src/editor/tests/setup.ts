import "@testing-library/jest-dom/vitest";
import { cleanup, configure } from "@testing-library/react";
import { afterEach } from "vitest";

// waitFor and findBy give up after 5 s, not the library's 1 s: a hosted CI runner can take longer than a second to
// answer a mocked request, and a condition that holds still returns as soon as it holds.
configure({ asyncUtilTimeout: 5000 });

afterEach(() => cleanup());
