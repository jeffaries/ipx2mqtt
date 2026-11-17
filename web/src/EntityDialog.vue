<script setup>
import { computed, ref, watch } from 'vue'
import { t } from './i18n'

const props = defineProps({
  modelValue: Boolean,
  item: Object,
  components: Array,
})
const emit = defineEmits(['update:modelValue', 'save'])

const form = ref(null)
const draft = ref({})
watch(
  () => [props.modelValue, props.item],
  () => {
    if (props.modelValue) draft.value = { ...(props.item ?? {}) }
  },
  { immediate: true },
)

const isCustom = computed(() => draft.value.Key !== undefined)
const component = computed(() => draft.value.Component)
const customComponents = computed(() => [{ title: t('d.componentDefault'), value: '' }, 'switch', 'light'])

const identifier = (v) => /^[A-Za-z0-9_-]+$/.test(v ?? '') || t('d.identifier')
const required = (v) => (v !== undefined && v !== null && String(v).trim() !== '') || t('d.required')

function bind(key, numeric = false) {
  return {
    modelValue: draft.value[key] ?? '',
    'onUpdate:modelValue': (v) => {
      draft.value[key] = numeric ? (v === '' || v === null ? undefined : Number(v)) : v
    },
  }
}

async function submit() {
  const { valid } = await form.value.validate()
  if (!valid) return
  if (isCustom.value) draft.value.Key = String(draft.value.Key).trim()
  if (!draft.value.Dimmable && draft.value.Component !== 'light') delete draft.value.Dimmable
  emit('save', { ...draft.value })
}
</script>

<template>
  <v-dialog :model-value="modelValue" max-width="720" scrollable persistent @update:model-value="emit('update:modelValue', $event)">
    <v-card :title="isCustom ? t('d.customTitle') : t('d.manualTitle')">
      <v-card-text>
        <v-form ref="form" @submit.prevent="submit">
          <v-row>
            <template v-if="isCustom">
              <v-col cols="12">
                <v-text-field v-bind="bind('Key')" :label="t('d.key')" :hint="t('d.keyHint')" persistent-hint :rules="[required]" />
              </v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Name')" :label="t('d.name')" :hint="t('d.nameHint')" persistent-hint /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Area')" :label="t('d.area')" :hint="t('d.areaHint')" persistent-hint /></v-col>
              <v-col cols="12" sm="6">
                <v-select v-bind="bind('Component')" :items="customComponents" :label="t('d.component')" />
              </v-col>
              <v-col v-if="component === 'light'" cols="12" sm="6">
                <v-switch
                  :model-value="draft.Dimmable !== false"
                  :label="t('d.dimmable')"
                  :hint="t('d.dimmableHint')"
                  persistent-hint
                  color="primary"
                  @update:model-value="draft.Dimmable = $event ? undefined : false"
                />
              </v-col>
            </template>

            <template v-else>
              <v-col cols="12" sm="4"><v-text-field v-bind="bind('Type')" :label="t('d.type')" :hint="t('d.typeHint')" persistent-hint :rules="[required, identifier]" /></v-col>
              <v-col cols="12" sm="4"><v-text-field v-bind="bind('Index', true)" type="number" :label="t('d.index')" :hint="t('d.indexHint')" persistent-hint /></v-col>
              <v-col cols="12" sm="4"><v-select v-bind="bind('Component')" :items="components" :label="t('d.component')" :rules="[required]" /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Name')" :label="t('d.name')" :rules="[required]" /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Area')" :label="t('d.area')" /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('StateId', true)" type="number" :label="t('d.stateId')" :hint="t('d.stateIdHint')" persistent-hint /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('CommandId', true)" type="number" :label="t('d.commandId')" :hint="t('d.commandIdHint')" persistent-hint /></v-col>
              <template v-if="component === 'light'">
                <v-col cols="12" sm="6"><v-text-field v-bind="bind('BrightnessStateId', true)" type="number" :label="t('d.brightnessStateId')" :hint="t('d.brightnessStateIdHint')" persistent-hint /></v-col>
                <v-col cols="12" sm="6"><v-text-field v-bind="bind('BrightnessCommandId', true)" type="number" :label="t('d.brightnessCommandId')" :hint="t('d.brightnessCommandIdHint')" persistent-hint /></v-col>
              </template>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('DeviceClass')" :label="t('d.deviceClass')" :hint="t('d.deviceClassHint')" persistent-hint /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Icon')" :label="t('d.icon')" :hint="t('d.iconHint')" persistent-hint /></v-col>
              <template v-if="component === 'sensor' || component === 'number'">
                <v-col cols="12" sm="4"><v-text-field v-bind="bind('Unit')" :label="t('d.unit')" /></v-col>
                <v-col cols="12" sm="4"><v-text-field v-bind="bind('Decimals', true)" type="number" :label="t('d.decimals')" /></v-col>
                <v-col v-if="component === 'sensor'" cols="12" sm="4"><v-text-field v-bind="bind('StateClass')" :label="t('d.stateClass')" :hint="t('d.stateClassHint')" persistent-hint /></v-col>
              </template>
              <template v-if="component === 'number'">
                <v-col cols="12" sm="6"><v-text-field v-bind="bind('Min', true)" type="number" :label="t('d.min')" placeholder="0" /></v-col>
                <v-col cols="12" sm="6"><v-text-field v-bind="bind('Max', true)" type="number" :label="t('d.max')" placeholder="100" /></v-col>
              </template>
              <v-col v-if="component === 'binary_sensor'" cols="12">
                <v-switch v-model="draft.Invert" :label="t('d.invert')" color="primary" hide-details />
              </v-col>
            </template>
          </v-row>
        </v-form>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="emit('update:modelValue', false)">{{ t('d.cancel') }}</v-btn>
        <v-btn color="primary" variant="flat" @click="submit">{{ t('d.ok') }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
