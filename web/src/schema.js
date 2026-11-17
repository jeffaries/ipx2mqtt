// Description of the config.yml settings shown in the interface. `path` is the key path in the YAML tree.
// Texts live in locales/*.js: section 's.<id>', group 'g.<id>' (+ 'n.<id>' note), field 'f.<path>' (+ 'h.<path>' hint).
export const sections = [
  {
    id: 'mqtt', icon: 'mdi-server-network',
    groups: [
      { id: 'mqtt', fields: [
        { path: 'Mqtt.Host', type: 'text', default: 'localhost' },
        { path: 'Mqtt.Port', type: 'number', default: 1883 },
        { path: 'Mqtt.Username', type: 'text' },
        { path: 'Mqtt.Password', type: 'password' },
        { path: 'Mqtt.UseTls', type: 'bool', default: false },
        { path: 'Mqtt.ClientId', type: 'text', default: 'ipx2mqtt' },
        { path: 'Mqtt.BaseTopic', type: 'text', default: 'ipx800' },
      ] },
      { id: 'ha', fields: [
        { path: 'Discovery.Prefix', type: 'text', default: 'homeassistant' },
        { path: 'Device.NodeId', type: 'text', default: 'ipx800' },
        { path: 'Device.Name', type: 'text', default: 'IPX800 V5' },
        { path: 'Device.Manufacturer', type: 'text', default: 'GCE Electronics' },
        { path: 'Device.Model', type: 'text', default: 'IPX800 V5' },
      ] },
    ],
  },
  {
    id: 'ipx', icon: 'mdi-lan-connect',
    groups: [
      { id: 'conn', fields: [
        { path: 'Ipx.BaseUrl', type: 'text', default: 'http://192.168.1.54' },
        { path: 'Ipx.Login', type: 'text', default: 'api' },
        { path: 'Ipx.Password', type: 'password' },
        { path: 'Ipx.ApiKey', type: 'password' },
        { path: 'Ipx.AllowInvalidCertificate', type: 'bool', default: false },
        { path: 'Ipx.AutoDiscover', type: 'bool', default: true },
      ] },
      { id: 'delays', fields: [
        { path: 'Ipx.RequestTimeoutSeconds', type: 'number', default: 10 },
        { path: 'Ipx.OfflineGraceSeconds', type: 'number', default: 5 },
      ] },
      { id: 'ws', fields: [
        { path: 'Ipx.WebSocket.Enabled', type: 'bool', default: true },
        { path: 'Ipx.WebSocket.ReceiveTimeoutSeconds', type: 'number', default: 30 },
        { path: 'Ipx.WebSocket.KeepAliveSeconds', type: 'number', default: 15 },
        { path: 'Ipx.WebSocket.ReconnectMinSeconds', type: 'number', default: 1 },
        { path: 'Ipx.WebSocket.ReconnectMaxSeconds', type: 'number', default: 60 },
      ] },
      { id: 'polling', fields: [
        { path: 'Ipx.Polling.IntervalSeconds', type: 'number', default: 10 },
      ] },
    ],
  },
  {
    id: 'services', icon: 'mdi-web',
    groups: [
      { id: 'push', fields: [
        { path: 'Push.Enabled', type: 'bool', default: false },
        { path: 'Push.Port', type: 'number', default: 8080 },
        { path: 'Push.Token', type: 'password' },
      ] },
      { id: 'web', fields: [
        { path: 'Web.Enabled', type: 'bool', default: true },
        { path: 'Web.Port', type: 'number', default: 8080 },
        { path: 'Web.Username', type: 'text', default: 'admin' },
        { path: 'Web.Password', type: 'password' },
      ] },
    ],
  },
]

export const componentTypes = ['switch', 'binary_sensor', 'sensor', 'light', 'number']

export function getPath(obj, path) {
  return path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj)
}

export function setPath(obj, path, value) {
  const keys = path.split('.')
  const last = keys.pop()
  const parent = keys.reduce((o, k) => (o[k] = o[k] && typeof o[k] === 'object' ? o[k] : {}), obj)
  parent[last] = value
}

// Removes empty values so that the written YAML only holds what was filled in.
export function prune(value) {
  if (Array.isArray(value)) return value.map(prune)
  if (value && typeof value === 'object') {
    const out = {}
    for (const [k, v] of Object.entries(value)) {
      const p = prune(v)
      const empty = p === '' || p === null || p === undefined || (typeof p === 'object' && !Array.isArray(p) && Object.keys(p).length === 0)
      if (!empty) out[k] = p
    }
    return out
  }
  return value
}
