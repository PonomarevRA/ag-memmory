import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

const rootDir = fileURLToPath(new URL('.', import.meta.url));

/** Vite builds the single browser-owned AgMemory SPA. */
export default defineConfig({
  base: '/dist/',
  build: {
    outDir: path.resolve(rootDir, '../wwwroot/dist'),
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: { input: path.resolve(rootDir, 'index.html') }
  },
  resolve: {
    alias: {
      '@shared': path.resolve(rootDir, 'src/shared'),
      '@vendor': path.resolve(rootDir, '../wwwroot/vendor')
    }
  }
});
