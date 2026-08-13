import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

const rootDir = fileURLToPath(new URL('.', import.meta.url));

/** Vite builds browser modules consumed by Blazor Interactive Server via IJSRuntime.import. */
export default defineConfig({
  build: {
    outDir: path.resolve(rootDir, '../wwwroot/dist'),
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      input: {
        chat: path.resolve(rootDir, 'src/features/chat/index.ts'),
        'memory-reader': path.resolve(rootDir, 'src/features/memory-reader/index.ts'),
        'memory-graph': path.resolve(rootDir, 'src/features/memory-graph/index.ts'),
        'memory-status': path.resolve(rootDir, 'src/features/memory-status/index.ts'),
        navigation: path.resolve(rootDir, 'src/features/navigation/index.ts'),
        'reconnect-modal': path.resolve(rootDir, 'src/layout/reconnect-modal.ts')
      },
      treeshake: false,
      output: {
        entryFileNames: '[name].js',
        chunkFileNames: 'chunks/[name]-[hash].js'
      }
    }
  },
  resolve: {
    alias: {
      '@shared': path.resolve(rootDir, 'src/shared'),
      '@vendor': path.resolve(rootDir, '../wwwroot/vendor')
    }
  }
});
