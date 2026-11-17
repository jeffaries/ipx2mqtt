<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useLocale } from 'vuetify'
import { sections, componentTypes, getPath, setPath, prune } from './schema'
import EntityDialog from './EntityDialog.vue'
import Monitor from './Monitor.vue'
import { languages, locale, setLocale, t, tOrNull } from './i18n'

const vuetifyLocale = useLocale()
watch(locale, (l) => { vuetifyLocale.current.value = l }, { immediate: true })
const currentLanguage = computed(() => languages.find((l) => l.code === locale.value))

const loading = ref(true)
const saving = ref(false)
const directory = ref(null)
const config = ref({})
const entities = ref([])
const original = ref('')
const tab = ref('mqtt')
const reveal = ref({})
const search = ref('')
const kindFilter = ref('all')
const snackbar = ref({ show: false, text: '', color: 'success' })
const errors = ref([])
const restartDialog = ref(false)
const dialog = ref({ show: false, index: -1, item: null })
const deleteTarget = ref(null)

const snapshot = () => JSON.stringify({ c: prune(config.value), e: prune(entities.value) })
const dirty = computed(() => !loading.value && snapshot() !== original.value)

function notify(text, color = 'success') {
  snackbar.value = { show: true, text, color }
}

async function load() {
  loading.value = true
  try {
    const res = await fetch('api/config')
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const data = await res.json()
    directory.value = data.directory
    config.value = data.config ?? {}
    entities.value = data.entities ?? []
    original.value = snapshot()
  } catch (e) {
    notify(t('app.loadError', { message: e.message }), 'error')
  } finally {
    loading.value = false
  }
}

async function save() {
  saving.value = true
  errors.value = []
  try {
    const res = await fetch('api/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ config: prune(config.value), entities: prune(entities.value) }),
    })
    const body = await res.json().catch(() => ({}))
    if (!res.ok) {
      errors.value = body.errors ?? [t('app.httpError', { status: res.status })]
      notify(t('app.invalid'), 'error')
      return
    }
    await load()
    restartDialog.value = true
  } catch (e) {
    notify(t('app.saveError', { message: e.message }), 'error')
  } finally {
    saving.value = false
  }
}

async function restart() {
  restartDialog.value = false
  try {
    await fetch('api/restart', { method: 'POST' })
    notify(t('app.restartRequested'))
  } catch (e) {
    notify(t('app.restartError', { message: e.message }), 'error')
  }
}

const fieldValue = (f) => {
  const v = getPath(config.value, f.path)
  return v === undefined || v === null ? (f.type === 'bool' ? !!f.default : '') : v
}
function updateField(f, v) {
  if (f.type === 'number') v = v === '' || v === null ? undefined : Number(v)
  setPath(config.value, f.path, v)
}

const kindOf = (e) => (e.Key ? 'custom' : 'manual')
const rows = computed(() =>
  entities.value
    .map((e, index) => ({ e, index }))
    .filter(({ e }) => kindFilter.value === 'all' || kindOf(e) === kindFilter.value)
    .map(({ e, index }) => ({
      index,
      kind: kindOf(e),
      ref: e.Key ? e.Key : `${e.Type ?? ''}/${e.Index ?? e.StateId ?? e.CommandId ?? ''}`,
      name: e.Name ?? '',
      area: e.Area ?? '',
      component: e.Component ?? '',
    })),
)
const headers = computed(() => [
  { title: t('e.colOrigin'), key: 'kind', width: 130 },
  { title: t('e.colRef'), key: 'ref' },
  { title: t('e.colName'), key: 'name' },
  { title: t('e.colArea'), key: 'area' },
  { title: t('e.colComponent'), key: 'component' },
  { title: '', key: 'actions', sortable: false, align: 'end', width: 110 },
])

function addEntity(kind) {
  dialog.value = { show: true, index: -1, item: kind === 'custom' ? { Key: '', Name: '' } : { Type: '', Name: '', Component: 'switch' } }
}
function editEntity(index) {
  dialog.value = { show: true, index, item: JSON.parse(JSON.stringify(entities.value[index])) }
}
function commitEntity(item) {
  if (dialog.value.index >= 0) entities.value[dialog.value.index] = item
  else entities.value.push(item)
  dialog.value.show = false
}
function confirmDelete() {
  entities.value.splice(deleteTarget.value, 1)
  deleteTarget.value = null
}

const warnUnload = (ev) => {
  if (dirty.value) ev.preventDefault()
}
onMounted(() => {
  load()
  window.addEventListener('beforeunload', warnUnload)
})
onBeforeUnmount(() => window.removeEventListener('beforeunload', warnUnload))
</script>

<template>
  <v-app>
    <v-app-bar color="primary" density="comfortable">
      <v-app-bar-title>
        <v-icon icon="mdi-home-automation" class="mr-2" />{{ t('app.title') }}
      </v-app-bar-title>
      <v-chip v-if="dirty" color="warning" variant="flat" class="mr-2" size="small">{{ t('app.dirty') }}</v-chip>
      <v-btn :disabled="!dirty || loading" variant="text" prepend-icon="mdi-undo" @click="load">{{ t('app.cancel') }}</v-btn>
      <v-btn :disabled="!dirty" :loading="saving" variant="flat" color="success" prepend-icon="mdi-content-save" class="ml-2 mr-2" @click="save">
        {{ t('app.save') }}
      </v-btn>
      <v-menu>
        <template #activator="{ props }">
          <v-btn v-bind="props" variant="text" :aria-label="t('app.language')" :title="t('app.language')">
            <span class="mr-1">{{ currentLanguage.flag }}</span>{{ currentLanguage.code.toUpperCase() }}
            <v-icon end icon="mdi-menu-down" />
          </v-btn>
        </template>
        <v-list density="compact" :selected="[locale]">
          <v-list-item v-for="l in languages" :key="l.code" :value="l.code" @click="setLocale(l.code)">
            <template #prepend><span class="mr-3">{{ l.flag }}</span></template>
            <v-list-item-title>{{ l.name }}</v-list-item-title>
          </v-list-item>
        </v-list>
      </v-menu>
    </v-app-bar>

    <v-main>
      <v-container :style="{ maxWidth: tab === 'monitor' ? '1600px' : '1100px' }">
        <v-progress-linear v-if="loading" indeterminate />

        <v-alert v-if="!loading && !directory" type="warning" class="mb-4" variant="tonal">
          {{ t('app.noDir') }}
        </v-alert>
        <div v-else-if="directory" class="text-caption text-medium-emphasis mb-2">
          {{ t('app.dirLabel') }} <code>{{ directory }}</code> {{ t('app.dirNote') }}
        </div>

        <v-alert v-if="errors.length" type="error" class="mb-4" closable @click:close="errors = []">
          <div v-for="(err, i) in errors" :key="i">{{ err }}</div>
        </v-alert>

        <v-tabs v-model="tab" color="primary" show-arrows>
          <v-tab v-for="s in sections" :key="s.id" :value="s.id" :prepend-icon="s.icon">{{ t('s.' + s.id) }}</v-tab>
          <v-tab value="entities" prepend-icon="mdi-lightbulb-group">
            {{ t('s.entities') }}
            <v-chip size="x-small" class="ml-2">{{ entities.length }}</v-chip>
          </v-tab>
          <v-tab value="monitor" prepend-icon="mdi-pulse">{{ t('s.monitor') }}</v-tab>
        </v-tabs>
        <v-divider class="mb-4" />

        <v-tabs-window v-model="tab">
          <v-tabs-window-item v-for="s in sections" :key="s.id" :value="s.id">
            <v-card v-for="g in s.groups" :key="g.id" class="mb-4" variant="outlined">
              <v-card-title class="text-subtitle-1">{{ t('g.' + g.id) }}</v-card-title>
              <v-card-subtitle v-if="tOrNull('n.' + g.id)" class="text-wrap">{{ t('n.' + g.id) }}</v-card-subtitle>
              <v-card-text>
                <v-row>
                  <v-col v-for="f in g.fields" :key="f.path" cols="12" :sm="f.type === 'bool' ? 12 : 6">
                    <v-switch
                      v-if="f.type === 'bool'"
                      :model-value="fieldValue(f)"
                      :label="t('f.' + f.path)"
                      :hint="tOrNull('h.' + f.path) ?? undefined"
                      persistent-hint
                      color="primary"
                      density="comfortable"
                      @update:model-value="updateField(f, !!$event)"
                    />
                    <v-text-field
                      v-else
                      :model-value="fieldValue(f)"
                      :label="t('f.' + f.path)"
                      :hint="tOrNull('h.' + f.path) ?? undefined"
                      :placeholder="f.default !== undefined ? String(f.default) : undefined"
                      :type="f.type === 'number' ? 'number' : f.type === 'password' && !reveal[f.path] ? 'password' : 'text'"
                      :append-inner-icon="f.type === 'password' ? (reveal[f.path] ? 'mdi-eye-off' : 'mdi-eye') : undefined"
                      persistent-hint
                      autocomplete="off"
                      @click:append-inner="reveal[f.path] = !reveal[f.path]"
                      @update:model-value="updateField(f, $event)"
                    />
                  </v-col>
                </v-row>
              </v-card-text>
            </v-card>
          </v-tabs-window-item>

          <v-tabs-window-item value="entities">
            <v-card variant="outlined">
              <v-card-text>
                <v-row align="center">
                  <v-col cols="12" md="4">
                    <v-text-field v-model="search" :label="t('e.search')" prepend-inner-icon="mdi-magnify" clearable hide-details />
                  </v-col>
                  <v-col cols="12" md="auto">
                    <v-btn-toggle v-model="kindFilter" mandatory color="primary" density="comfortable" variant="outlined" divided>
                      <v-btn value="all">{{ t('e.all') }}</v-btn>
                      <v-btn value="custom">{{ t('e.custom') }}</v-btn>
                      <v-btn value="manual">{{ t('e.manual') }}</v-btn>
                    </v-btn-toggle>
                  </v-col>
                  <v-col cols="12" md="auto" class="ml-md-auto">
                    <v-menu>
                      <template #activator="{ props }">
                        <v-btn v-bind="props" color="primary" prepend-icon="mdi-plus">{{ t('e.add') }}</v-btn>
                      </template>
                      <v-list>
                        <v-list-item prepend-icon="mdi-tune" :title="t('e.addCustom')" :subtitle="t('e.addCustomSub')" @click="addEntity('custom')" />
                        <v-list-item prepend-icon="mdi-pencil-plus" :title="t('e.addManual')" :subtitle="t('e.addManualSub')" @click="addEntity('manual')" />
                      </v-list>
                    </v-menu>
                  </v-col>
                </v-row>
              </v-card-text>
              <v-data-table :headers="headers" :items="rows" :search="search" item-value="index" density="comfortable" :items-per-page="25" hover>
                <template #item.kind="{ item }">
                  <v-chip size="small" :color="item.kind === 'custom' ? 'primary' : 'secondary'" variant="tonal">
                    {{ item.kind === 'custom' ? t('e.kindCustom') : t('e.kindManual') }}
                  </v-chip>
                </template>
                <template #item.ref="{ item }"><code>{{ item.ref }}</code></template>
                <template #item.actions="{ item }">
                  <v-btn icon="mdi-pencil" size="small" variant="text" @click="editEntity(item.index)" />
                  <v-btn icon="mdi-delete" size="small" variant="text" color="error" @click="deleteTarget = item.index" />
                </template>
                <template #no-data>{{ t('e.noData') }}</template>
              </v-data-table>
            </v-card>
            <div class="text-caption text-medium-emphasis mt-2">
              {{ t('e.componentNote') }}
            </div>
          </v-tabs-window-item>
          <v-tabs-window-item value="monitor">
            <monitor />
          </v-tabs-window-item>
        </v-tabs-window>
      </v-container>
    </v-main>

    <entity-dialog v-model="dialog.show" :item="dialog.item" :components="componentTypes" @save="commitEntity" />

    <v-dialog :model-value="deleteTarget !== null" max-width="420" @update:model-value="deleteTarget = null">
      <v-card :title="t('e.deleteTitle')">
        <v-card-text>{{ t('e.deleteText') }}</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="deleteTarget = null">{{ t('app.cancel') }}</v-btn>
          <v-btn color="error" variant="flat" @click="confirmDelete">{{ t('e.delete') }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-dialog v-model="restartDialog" max-width="480">
      <v-card :title="t('r.title')">
        <v-card-text>
          {{ t('r.text') }}
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="restartDialog = false">{{ t('r.later') }}</v-btn>
          <v-btn color="primary" variant="flat" prepend-icon="mdi-restart" @click="restart">{{ t('r.now') }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar v-model="snackbar.show" :color="snackbar.color" timeout="6000" multi-line>{{ snackbar.text }}</v-snackbar>
  </v-app>
</template>
