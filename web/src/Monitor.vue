<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { t } from './i18n'

const MAX_ROWS = 1000

const events = ref([]) // newest first
const pending = ref([]) // received while paused
const paused = ref(false)
const status = ref('connecting') // connecting | connected | disconnected
const search = ref('')
const hideDiscovery = ref(true)
const groups = ref(['ipx', 'mqtt', 'system'])

let connection = null

const groupOf = (e) => (e.kind.startsWith('ipx') ? 'ipx' : e.kind.startsWith('mqtt') ? 'mqtt' : 'system')
const flows = {
  'ipx.state': { from: 'IPX', to: 'ipx2mqtt', color: 'orange' },
  'ipx.command': { from: 'ipx2mqtt', to: 'IPX', color: 'deep-orange' },
  'mqtt.publish': { from: 'ipx2mqtt', to: 'MQTT', color: 'blue' },
  'mqtt.receive': { from: 'MQTT', to: 'ipx2mqtt', color: 'indigo' },
}

function toRow(e) {
  const flow = flows[e.kind]
  return {
    seq: e.seq,
    kind: e.kind,
    group: groupOf(e),
    time: new Date(e.time).toLocaleTimeString(undefined, { hour12: false }) + '.' + String(new Date(e.time).getMilliseconds()).padStart(3, '0'),
    flow,
    topic: e.topic ?? '',
    ipxId: e.ipxId,
    payload: e.payload ?? '',
    retain: e.retain === true,
    ok: e.ok,
    detail: e.detail ?? '',
  }
}

function add(list) {
  const rows = list.map(toRow)
  if (paused.value) {
    pending.value = [...rows.reverse(), ...pending.value].slice(0, MAX_ROWS)
  } else {
    events.value = [...rows.reverse(), ...events.value].slice(0, MAX_ROWS)
  }
}

function resume() {
  paused.value = false
  events.value = [...pending.value, ...events.value].slice(0, MAX_ROWS)
  pending.value = []
}

function clear() {
  events.value = []
  pending.value = []
}

const isDiscovery = (r) => r.kind.startsWith('mqtt.') && /\/config$/.test(r.topic)

const visible = computed(() => {
  const q = search.value.trim().toLowerCase()
  return events.value.filter(
    (r) =>
      groups.value.includes(r.group) &&
      !(hideDiscovery.value && isDiscovery(r)) &&
      (!q || `${r.topic} ${r.payload} ${r.ipxId ?? ''} ${r.detail}`.toLowerCase().includes(q)),
  )
})

const headers = computed(() => [
  { title: t('m.colTime'), key: 'time', width: 130, sortable: false },
  { title: t('m.colFlow'), key: 'flow', width: 210, sortable: false },
  { title: t('m.colTopic'), key: 'topic', sortable: false },
  { title: t('m.colValue'), key: 'payload', sortable: false },
  { title: t('m.colStatus'), key: 'status', width: 150, sortable: false },
])

onMounted(async () => {
  connection = new HubConnectionBuilder()
    .withUrl('hubs/monitor')
    .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build()
  connection.on('backlog', (list) => {
    // After a reconnection the server sends its whole buffer again: only keep what we have not seen.
    const last = events.value[0]?.seq ?? pending.value[0]?.seq ?? 0
    add(list.filter((e) => e.seq > last))
  })
  connection.on('event', (e) => {
    const last = events.value[0]?.seq ?? pending.value[0]?.seq ?? 0
    if (e.seq > last) add([e])
  })
  connection.onreconnecting(() => (status.value = 'connecting'))
  connection.onreconnected(() => (status.value = 'connected'))
  connection.onclose(() => (status.value = 'disconnected'))
  try {
    await connection.start()
    status.value = connection.state === HubConnectionState.Connected ? 'connected' : 'disconnected'
  } catch {
    status.value = 'disconnected'
  }
})

onBeforeUnmount(() => connection?.stop())
</script>

<template>
  <v-card variant="outlined">
    <v-card-text>
      <v-row align="center">
        <v-col cols="12" md="3">
          <v-text-field v-model="search" :label="t('m.search')" prepend-inner-icon="mdi-magnify" clearable hide-details />
        </v-col>
        <v-col cols="12" md="auto">
          <v-btn-toggle v-model="groups" multiple color="primary" density="comfortable" variant="outlined" divided>
            <v-btn value="ipx">IPX</v-btn>
            <v-btn value="mqtt">MQTT</v-btn>
            <v-btn value="system">{{ t('m.filterSystem') }}</v-btn>
          </v-btn-toggle>
        </v-col>
        <v-col cols="12" md="auto">
          <v-switch v-model="hideDiscovery" :label="t('m.hideDiscovery')" color="primary" density="compact" hide-details />
        </v-col>
        <v-col cols="12" md="auto" class="ml-md-auto d-flex align-center ga-2">
          <v-chip :color="status === 'connected' ? 'success' : status === 'connecting' ? 'warning' : 'error'" variant="flat" size="small">
            {{ t('m.' + status) }}
          </v-chip>
          <v-btn v-if="!paused" prepend-icon="mdi-pause" variant="outlined" @click="paused = true">{{ t('m.pause') }}</v-btn>
          <v-btn v-else prepend-icon="mdi-play" color="primary" variant="flat" @click="resume">
            {{ t('m.resume') }} ({{ pending.length }})
          </v-btn>
          <v-btn prepend-icon="mdi-broom" variant="outlined" @click="clear">{{ t('m.clear') }}</v-btn>
        </v-col>
      </v-row>
    </v-card-text>

    <v-data-table-virtual :headers="headers" :items="visible" item-value="seq" density="compact" height="560" fixed-header hover>
      <template #item.time="{ item }"><span class="text-medium-emphasis text-no-wrap">{{ item.time }}</span></template>
      <template #item.flow="{ item }">
        <v-chip v-if="item.flow" :color="item.flow.color" size="small" variant="tonal" label class="text-no-wrap">
          {{ item.flow.from }}
          <v-icon icon="mdi-arrow-right" size="small" class="mx-1" />
          {{ item.flow.to }}
        </v-chip>
        <v-chip v-else size="small" variant="tonal" label>{{ t('m.filterSystem') }}</v-chip>
      </template>
      <template #item.topic="{ item }">
        <code v-if="item.topic">{{ item.topic }}</code>
        <span v-if="item.ipxId != null" class="text-medium-emphasis ml-2 text-no-wrap">#{{ item.ipxId }}</span>
        <span v-if="!item.topic && item.ipxId == null">{{ item.detail }}</span>
      </template>
      <template #item.payload="{ item }">
        <span class="text-break" style="font-family: monospace">{{ item.payload }}</span>
      </template>
      <template #item.status="{ item }">
        <v-chip v-if="item.retain" size="x-small" variant="outlined" class="mr-1">{{ t('m.retained') }}</v-chip>
        <v-chip v-if="item.ok === false" size="x-small" color="error" variant="flat" :title="item.detail">{{ t('m.failed') }}</v-chip>
        <v-icon v-else-if="item.ok === true" icon="mdi-check" color="success" size="small" />
      </template>
      <template #no-data>{{ t('m.empty') }}</template>
    </v-data-table-virtual>
  </v-card>
</template>
