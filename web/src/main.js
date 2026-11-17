import { createApp } from 'vue'
import { createVuetify } from 'vuetify'
import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import App from './App.vue'
import { locale, vuetifyMessages } from './i18n'

const vuetify = createVuetify({
  locale: { locale: locale.value, fallback: 'en', messages: vuetifyMessages },
  theme: { defaultTheme: 'light' },
  defaults: {
    VTextField: { variant: 'outlined', density: 'comfortable' },
    VSelect: { variant: 'outlined', density: 'comfortable' },
  },
})

createApp(App).use(vuetify).mount('#app')
