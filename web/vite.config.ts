import {defineConfig} from 'vite';

export default defineConfig({
  server: {
    port: 5174,
    strictPort: true,
    proxy: {'/api': {target: 'http://127.0.0.1:5080', changeOrigin: true}},
  },
});
