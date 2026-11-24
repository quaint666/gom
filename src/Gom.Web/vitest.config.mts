import { defineConfig } from 'vitest/config';

// Runs the page logic in jsdom. It cannot see layout, real focus or the real server: the .NET tests cover the API,
// and the pages were also opened in a real browser by hand.
export default defineConfig({
  test: {
    environment: 'jsdom',
    include: ['ScriptTests/**/*.test.ts'],
    setupFiles: ['ScriptTests/setup.ts'],
    restoreMocks: true,
  },
});
