import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import vuetify from 'vite-plugin-vuetify'

// The build goes straight into the ASP.NET project's wwwroot (see Dockerfile).
export default defineConfig({
  plugins: [vue(), vuetify({ autoImport: true })],
  build: { outDir: '../src/Ipx2Mqtt.Service/wwwroot', emptyOutDir: true },
  // `npm run dev`: proxy the API to a locally running service (dotnet run, Web.Port 8080)
  server: { proxy: { '/api': 'http://localhost:8080' } },
})
