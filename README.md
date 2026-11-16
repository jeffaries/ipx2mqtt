# ipx2mqtt

**IPX800 V5 → MQTT** gateway for **Home Assistant**, written in C# (.NET 10).

The service reads the state of the IPX800 V5 (relays, digital inputs, analog inputs, EBX extensions), publishes it
to an MQTT broker and declares the entities to Home Assistant through **MQTT device discovery**
(*MQTT Discovery*). No HACS integration, no Node-RED.

It is the first of a series of adapters (Daikin, HomeWizard…): everything related to MQTT / discovery /
availability is isolated in `Ipx2Mqtt.Core` and does not depend on the IPX.

> **IPX API documentation**: the official GCE OpenAPI files are kept in
> [docs/ipx-api/](docs/ipx-api/) and the protocol used by this service (encrypted login, WebSocket, commands)
> is summarised in [docs/ipx-protocol.md](docs/ipx-protocol.md).

## Contents

- [How it works](#how-it-works)
- [Quick start (Docker)](#quick-start-docker)
- [Configuration](#configuration)
- [Declaring entities](#declaring-entities)
- [MQTT topics](#mqtt-topics)
- [Environment variables](#environment-variables-optional)
- [IPX Push object (optional)](#ipx-push-object-optional)
- [Troubleshooting with MQTT Explorer](#troubleshooting-with-mqtt-explorer)
- [Development](#development)
- [Known limitations](#known-limitations)

## How it works

```
                 WebSocket (main source)
   IPX800 V5 ─────────────────────────────────┐
        │   REST /api/core/* (polling, fallback)│    ┌───────────┐        ┌────────────────┐
        │   REST PUT (commands)               ├────► │ ipx2mqtt  │ ─────► │ Mosquitto      │ ◄──► Home Assistant
        └─────────────────────────────────────┘      └───────────┘  MQTT  └────────────────┘     (MQTT Discovery)
```

- **Main source: the WebSocket of the IPX web interface.** The client logs in with the same encrypted login as
  the web interface (AES-ECB), reconnects automatically with a capped exponential *backoff*, detects a dead
  connection (no message received for `ReceiveTimeoutSeconds`) and **resynchronises the complete state on every
  (re)connection**.
- **Fallback: REST polling** (`GET /api/core/io` and `/api/core/ana`). It starts automatically as soon as the
  WebSocket is unavailable and stops as soon as it is back.
- **Commands** (`.../set`): sent over REST (`PUT /api/core/io/{id}`, `PUT /api/core/ana/{id}`).
  The state is published **only once the IPX confirms it** (through the WebSocket or the polling): a refused or
  lost command never leaves Home Assistant in a wrong state.
- **Availability** (`ipx800/status`): `offline` only when **both sources** are unavailable
  (with a short grace delay, `OfflineGraceSeconds`, so that the WebSocket → REST hand-over does not make the
  entities flap). MQTT Last Will: `offline`.
- **Only changes** are published, *retained*.
- **Home Assistant restarts**: the service subscribes to `homeassistant/status` and, when it receives `online`,
  republishes the discovery (after a random 1 to 5 s delay) and then all the current states.
- The WebSocket is **not an official API**: an unknown or malformed message is ignored (`Debug` log),
  counted, and summarised in an aggregated `Warning`: see [Known limitations](#known-limitations).

## Quick start (Docker)

1. Edit the two files of the [config/](config/) folder: [config.yml](config/config.yml) (MQTT broker, IPX address
   and credentials) and [entities.yml](config/entities.yml) (labels and entities). Templates:
   [config.example.yml](config/config.example.yml) and [entities.example.yml](config/entities.example.yml).
2. Start:

```bash
docker compose up -d --build
docker compose logs -f ipx2mqtt
```

At startup the logs should show: connection to the MQTT broker, `Logged in to the IPX`,
`IPX WebSocket connected`, `IPX full state resynchronised (N objects)`, then `IPX is now available`.
The entities appear in Home Assistant (*Settings → Devices & services → MQTT*) under the
"IPX800 V5" device.

### IPX user (required) and API key (optional)

**Create a dedicated user on the IPX with the Operator rights (level 2)** and put its login and password in
`Ipx.Login` / `Ipx.Password` of `config.yml`. Do not reuse an administrator account: the service only needs to read
the objects and to write relays and levels.

Why a user is required: the IPX WebSocket, which gives instant state changes, only accepts a session token
(`AuthToken`), and the only way to get one is to log in with a user account. The service therefore always needs
`Login` and `Password` as long as `Ipx.WebSocket.Enabled` is `true`.

Why an API key may be added: it is **optional**. Without it, the service uses the session token for the REST calls
too (reading the state, discovery, commands) and logs in again by itself when the token expires. With
`Ipx.ApiKey` set, the REST calls use the key instead of the token, which does not expire and does not depend on
the login. The WebSocket still logs in with the user, so the user is needed in both cases. An API key is created by
an administrator on the IPX; a key of level 1 (User) is enough to read and to send commands, level 0 (Supervisor)
only reads.

| Setup | What is needed |
|---|---|
| WebSocket + REST (recommended) | an Operator user (level 2); the API key is optional |
| REST polling only (`Ipx.WebSocket.Enabled: false`) | either an Operator user, or only an API key of level 1 |

The minimum levels are the ones given by the GCE OpenAPI files (reading: level 0, commands: level 1, login:
level 2); lower levels were not tested on real hardware. Never commit these secrets: `config/config.yml` contains
passwords and is ignored by git.

### Published image (GitHub Container Registry)

The workflow [.github/workflows/docker.yml](.github/workflows/docker.yml) runs the tests on every push and
pull request, then publishes the image to `ghcr.io/<user>/<repository>` from `main` (tags `latest`, `main`,
`sha-…`) and from `v1.2.3` tags (tags `1.2.3` and `1.2`). No secret to create. To use it, replace
`build: .` with `image: ghcr.io/<user>/<repository>:latest` in `docker-compose.yml`. If the repository is private,
log in first: `docker login ghcr.io` with a token that has the `read:packages` scope. The image is built for
`linux/amd64` only.

## Configuration

All the configuration lives in **two YAML files**, outside the application, in the [config/](config/) folder:

| File | Content |
|---|---|
| [config/config.yml](config/config.yml) | `Mqtt`, `Discovery`, `Device`, `Ipx`, `Push` (table below). Template: [config/config.example.yml](config/config.example.yml); the real file is ignored by git |
| [config/entities.yml](config/entities.yml) | `Entities` (customisation of discovered entities, and hand-declared entities). Documented template: [config/entities.example.yml](config/entities.example.yml) |

**Docker**: the folder is mounted read-only on `/config` (`./config:/config:ro` in
[docker-compose.yml](docker-compose.yml), which contains no environment variable). The service reads
`config.yml` and `entities.yml` from there. Outside Docker it looks for the folder, first match wins: the
`IPX2MQTT_CONFIG_DIR` environment variable, `/config`, then `config/` in the current directory or one of its
parents (F5 in VS Code). The startup log says "Configuration loaded from …". Restart the service after a change.

Precedence: `appsettings.json` (logging) < `config.yml` < `entities.yml` < environment variables
(`Section__Key`, optional) < command line.

| Section | Key | Default | Purpose |
|---|---|---|---|
| `Mqtt` | `Host`, `Port` | `localhost`, `1883` | MQTT broker |
| | `Username`, `Password`, `UseTls` | — | Authentication / TLS |
| | `ClientId` | `ipx2mqtt` | Client id (unique per instance) |
| | `BaseTopic` | `ipx800` | **Prefix of all topics** |
| `Discovery` | `Prefix` | `homeassistant` | Home Assistant discovery prefix |
| | `StatusTopic` | `homeassistant/status` | Home Assistant birth topic |
| `Device` | `NodeId` | `ipx800` | Device id (letters, digits, `_`, `-`) |
| | `Name`, `Manufacturer`, `Model` | `IPX800 V5`… | Device card in Home Assistant |
| `Ipx` | `BaseUrl` | `http://192.168.1.54` | IPX URL (`https://` → `wss://` automatically) |
| | `Login`, `Password` | `api`, — | IPX user (encrypted login, required by the WebSocket) |
| | `ApiKey` | — | Official API key (optional): when set, REST calls use it |
| | `RequestTimeoutSeconds` | `10` | REST request timeout |
| | `AutoDiscover` | `true` | Expose every relay, dimmer and analog output automatically |
| | `OfflineGraceSeconds` | `5` | Delay before the IPX is declared offline |
| | `AllowInvalidCertificate` | `false` | Accept a self-signed certificate |
| `Ipx:WebSocket` | `Enabled` | `true` | Disable to use polling only |
| | `ReceiveTimeoutSeconds` | `30` | Silence tolerated before the connection is considered dead |
| | `KeepAliveSeconds` | `15` | WebSocket keep-alive |
| | `ReconnectMinSeconds` / `ReconnectMaxSeconds` | `1` / `60` | Bounds of the exponential *backoff* |
| `Ipx:Polling` | `IntervalSeconds` | `10` | Polling period (fallback, only active while the WebSocket is down) |
| `Push` | `Enabled`, `Port`, `Token` | `false`, `8080` | [Push endpoint](#ipx-push-object-optional) |

The IPX publishes its clock every second on the WebSocket; this is what allows a dead connection to be detected
quickly. Objects that are not exposed (clock, diagnostics…) are ignored as soon as they arrive.

## Declaring entities

### Automatic discovery (default)

At startup the service reads the list of IPX objects (`/api/core/io`, `/api/core/ana`) and exposes **all** the
relays, dimmers and analog outputs it finds, without declaring anything. The IPX does not return the object `_id`s
in the description of its extensions: they are deduced from the **object names**, which follow fixed patterns.

| IPX objects (name) | Home Assistant entity | Type / index (topics) |
|---|---|---|
| `[01-IPX]Relay cmd n` + `Relay state n` | `switch` | `relay/n` |
| `[IPX]Open coll. n` + `Open coll. state n` | `switch` | `collector/n` |
| `[03-8R]Relay cmd n` + `Relay state n` (X-8R) | `switch` | `03-8r/n` |
| `[06-DM]On/Off n` + `Consign n` + `Position n` (dimmer) | `light` (on/off + 0-100 level) | `06-dm/n` |
| `[06-DM]On/Off All` | `switch` (all channels) | `06-dm-all/1` |
| `ana` objects linked to an X-010V, X-PWM or X-400 | `number` (0-100) | `x010v`, `xpwm`, `x400` |

Entities declared in `Entities` (below) **take precedence**: a discovered entity that uses the same IPX ids is
ignored. If the IPX is unreachable at startup, the service retries every 5 s (the MQTT discovery is only published
once the list is complete). To disable: `AutoDiscover: false` under `Ipx` in config.yml.
The startup log says "N declared + M auto-discovered entities".

> The X-010V / X-PWM / X-400 outputs could not be validated (not present on the test IPX).

### Customising discovered entities: `Entities` with a `Key`

Everything lives in the single `Entities` list of [config/entities.yml](config/entities.yml). An entry with a `Key`
customises an auto-discovered channel; an entry without `Key` declares an entity by hand (next section).

```yaml
Entities:
  - { Key: "04-8R.05", Name: Z3 first floor }                                  # rename only
  - { Key: "03-8R.02", Name: Office lights, Component: light }                 # relay shown as an on/off light
  - { Key: "06-DM.01", Name: Dining room }                                     # dimmer: dimmable light (default)
  - { Key: "06-DM.02", Name: Living spots, Component: light, Dimmable: false } # dimmer used as an on/off light
  - { Key: "06-DM.03", Name: Bedroom, Component: switch }                      # dimmer used as a plain switch
```

| Field | Meaning |
|---|---|
| `Key` | Module code `<module>.<channel>` (`01-IPX.03`, `04-8R.05`, `06-DM.01`…), i.e. the IPX object name `[04-8R]Relay cmd 5`; or the **numeric `_id` of the command object** (`"65536"`), which does not depend on object names and wins over the module code |
| `Name` | Name shown in Home Assistant (default: generic, e.g. "03-8R relais 3") |
| `Component` | `switch` or `light`. Default: `switch` for a relay, `light` for a dimmer |
| `Dimmable` | `light` only. `true` (default) = on/off + brightness slider (dimmers only); `false` = on/off light. A relay shown as a light is always on/off |

For Home Assistant: a dimmable light is published with `brightness_command_topic`/`brightness_state_topic`
(scale 0–100); an on/off light has only the command and state topics, which Home Assistant treats as an on/off light.
Renaming updates the entity in place (the `unique_id` does not change). Changing `Component` moves the entity to
another Home Assistant platform, so it is recreated (new `entity_id`, history not carried over). The discovery message
is only rebuilt at startup.

### Manual declaration

The list of exposed entities can also come from the configuration (`Entities` in `entities.yml`).

```yaml
Entities:
  - { Type: relay,  Index: 1, Name: Garage light, Component: switch, StateId: 65544, CommandId: 65536 }
  - { Type: input,  Index: 1, Name: Garage door, Component: binary_sensor, StateId: 65552, DeviceClass: door }
  - { Type: analog, Index: 1, Name: Cellar temperature, Component: sensor, StateId: 262153,
      Unit: "°C", DeviceClass: temperature, StateClass: measurement, Decimals: 1 }
```

### Finding the `_id`s

On the IPX V5, **every object has a numeric `_id`**, and the command and the state of a relay are
**two separate objects**:

| IPX object | `_id` (example) |
|---|---|
| `[01-IPX]Relay cmd 1` (command) | 65536 |
| `[01-IPX]Relay state 1` (state) | 65544 |
| `[01-IPX]Digital input 1` | 65552 |
| `[IPX]Analog input 1` | 262144 |

To list all the objects with their names (replace the host and the token; or use an API key with
`?ApiKey=...`):

```bash
curl "http://IPX/api/core/io?AuthToken=<token>"    # relays, inputs, logic objects ("on" field)
curl "http://IPX/api/core/ana?AuthToken=<token>"   # analog values ("value" field)
```

Id ranges: `io` 65536–131071, `ana` 196608–393215, `str` 524288–851967.

### Entity fields

| Field | Required | Description |
|---|---|---|
| `Type` | yes | Label used in the topics (`relay`, `input`, `analog`, `dimmer`…) |
| `Index` | no | Number used in the topics. Default: `StateId` (or `CommandId`) |
| `Name` | yes | Name shown in Home Assistant |
| `Component` | yes | `switch`, `binary_sensor`, `sensor`, `light`, `number` |
| `StateId` | depends on the component | IPX object holding the state |
| `CommandId` | `switch`, `light`, `number` | IPX object to write to send a command |
| `DeviceClass`, `Icon` | no | Home Assistant device class / icon |
| `Unit`, `StateClass`, `Decimals` | no | `sensor`: unit, state class, number of published decimals |
| `Invert` | no | `binary_sensor`: inverts ON/OFF |
| `BrightnessStateId`, `BrightnessCommandId` | no | `light`: analog objects (0–100) of the level |
| `Min`, `Max` | no | `number`: accepted range (default 0 / 100) |

### Supported components

| `Component` | Use | Requirements |
|---|---|---|
| `switch` | IPX, X-8R relays… | `CommandId` (and `StateId`, otherwise the state is read from `CommandId`) |
| `binary_sensor` | Digital inputs, states | `StateId` |
| `sensor` | Analog inputs, measurements | `StateId` |
| `number` | Sliders: analog outputs | `CommandId`; `Min` / `Max` (default 0 / 100) |
| `light` | Dimmers (X-DIMMER, X-DM) | `CommandId`; `BrightnessCommandId` / `BrightnessStateId` for the level |

Dimmer example (`[06-DM]On/Off 1` = io 65587, `[06-DM]Consign 1` = ana 262194, `[06-DM]Position 1` = ana 196617):

```yaml
- { Type: dimmer, Index: 1, Name: Living room, Component: light,
    StateId: 65587, CommandId: 65587, BrightnessCommandId: 262194, BrightnessStateId: 196617 }
```

### Adding an extension or a component

Each Home Assistant platform is a small `IComponentMapper`
([ComponentMappers.cs](src/Ipx2Mqtt.Ipx/Mapping/ComponentMappers.cs)) registered in
[IpxServiceCollectionExtensions.cs](src/Ipx2Mqtt.Ipx/IpxServiceCollectionExtensions.cs). For an X-4VR roller
shutter (`cover`), just add a mapper that associates the `ioCommandUp/Down/Stop` and
`anaPosition/anaCommand` objects: the rest (WebSocket, MQTT, discovery, state diffing) does not change.

## MQTT topics

With the default `ipx800` prefix:

| Topic | Direction | Content |
|---|---|---|
| `ipx800/status` | published, *retained* | `online` / `offline` (Last Will: `offline`) |
| `ipx800/<type>/<index>/state` | published, *retained* | `ON`/`OFF` or a numeric value (e.g. `ipx800/relay/1/state`) |
| `ipx800/<type>/<index>/set` | subscribed | `ON` / `OFF` (or a number for a `number`) |
| `ipx800/<type>/<index>/brightness/state` | published, *retained* | level 0–100 (`light`) |
| `ipx800/<type>/<index>/brightness/set` | subscribed | level 0–100 (`light`) |
| `homeassistant/device/<node_id>/config` | published, *retained* | device discovery |
| `homeassistant/status` | subscribed | `online` → republishes the discovery, then the states |

The discovery is **a single message** per device (`dev`, `o`, `cmps`). The state and command topics are declared
**in each component** (root-level options would apply to every component); only the availability is shared at the
root.

Every published message is *retained*. States are published only when their value changes, and all known states
are sent again after an MQTT reconnection and when Home Assistant restarts.

**Cleanly removing the entities**: publish an **empty** *retained* message on the discovery topic:

```bash
mosquitto_pub -h <broker> -t homeassistant/device/ipx800/config -r -n
```

## Environment variables (optional)

The YAML files are enough; any key can additionally be overridden with an environment variable.

Any configuration key is overridden with `Section__Key` (two underscores). Lists use the index:

| Variable | Example |
|---|---|
| `Mqtt__Host` | `mosquitto` |
| `Mqtt__Username` / `Mqtt__Password` | `ha` / `secret` |
| `Mqtt__BaseTopic` | `home/ipx` |
| `Ipx__BaseUrl` | `http://192.168.1.54` |
| `Ipx__Login` / `Ipx__Password` | `api` / `secret` |
| `Ipx__ApiKey` | official API key (optional) |
| `Ipx__WebSocket__Enabled` | `false` to force polling |
| `Ipx__Polling__IntervalSeconds` | `5` |
| `Device__NodeId` | `ipx800` |
| `Entities__0__Type` / `Entities__0__Name` / `Entities__0__Component` | `relay` / `Garage` / `switch` |
| `Entities__0__StateId` / `Entities__0__CommandId` | `65544` / `65536` |
| `IPX2MQTT_CONFIG_DIR` | folder holding `config.yml` and `entities.yml` |
| `Logging__LogLevel__Default` | `Debug` to see every ignored message and every change |

## IPX Push object (optional)

The WebSocket is normally enough. The *push* endpoint is a **complementary source**: the IPX **Push** object
sends an HTTP request to the service whenever an object you associate with it changes.

> ⚠️ **Feature not validated on real hardware.** The exact format of the request sent by the Push object
> depends on its configuration; the two forms below are the ones the service can read.

1. Enable the endpoint: `Push.Enabled: true` and `Push.Port: 8080` in `config.yml` (publish the port in Docker) and,
   preferably, `Push.Token: <secret>`.
2. On the IPX, create a **Push** object (*Objects → Push*):
   - **Address**: the IP address of the machine hosting ipx2mqtt, **port** `8080`, method `GET`.
   - **URI ON**: `/push/65544/on?token=<secret>` · **URI OFF**: `/push/65544/off?token=<secret>`
     (`65544` = `_id` of the state object, here the state of relay 1).
   - Link the **On** input of the Push object to the object whose changes you want to follow.
3. For a JSON call, use `POST /push` with a body `{"_id": 65544, "on": true}` or
   `{"_id": 262153, "value": 9.2}`, and the token in the `X-Push-Token` header.

| Request | Effect |
|---|---|
| `GET\|POST\|PUT /push/{id}/{value}` | `value` = `on`, `off`, `true`, `false` or a number |
| `POST /push` | JSON body `{"_id":…, "on":…}` or `{"_id":…, "value":…}` |

## Troubleshooting with MQTT Explorer

With [MQTT Explorer](https://mqtt-explorer.com/), connect to the broker and look at the tree:

1. **`ipx800/status`**
   - `online`: everything works.
   - `offline`: the IPX is unreachable through **both** sources (or the service is stopped). Check the logs:
     `docker compose logs ipx2mqtt`. Check `Ipx.BaseUrl`, the login and the password
     (`IPX secure login failed` = credentials refused).
   - missing: the service never connected to the broker (`Mqtt.Host`, MQTT credentials).
2. **`homeassistant/device/<node_id>/config`**: the discovery JSON must exist (*retained* box). If it is missing,
   the entities will not appear in Home Assistant. Check that `homeassistant/status` is the birth topic configured
   in the Home Assistant MQTT integration.
3. **`ipx800/<type>/<index>/state`**: `ON`/`OFF` or numeric values. If a topic is missing, the configured `_id` is
   probably wrong (objects that are not exposed are ignored). Switch the logs to `Debug` to see the changes.
4. **Testing a command**: publish `ON` on `ipx800/relay/1/set`. The log must show
   `Command sent to IPX object 65536`, then the state must switch to `ON` **when the IPX confirms it**. If the state
   does not change: the IPX user probably is not allowed to write (`403` error) or the command was refused
   (see the log).
5. **Entities "unavailable" in Home Assistant**: `ipx800/status` is `offline`, or the `BaseTopic` prefix differs
   between the discovery and the actual topics.
6. **Duplicated entities after changing `Index`, `Type` or `NodeId`**: remove the old discovery (empty *retained*
   message, see above).
7. **Ignored messages**: a `Warning` "*N IPX message(s) could not be parsed*" reports an unexpected message format
   (see [Known limitations](#known-limitations)). Switch to `Debug` to see each ignored message.
8. **`IPX WebSocket connection failed … status code '200' when status code '101' was expected`**: the IPX answered
   the WebSocket request with its web page instead of upgrading the connection. The service keeps working through
   the REST polling (state refreshed every `Polling.IntervalSeconds`). The IPX has a limited number of connections
   (see the `[DIAG]NB CONNECTIONS RUNNING` analog object): close other clients, restart the IPX, or set
   `Ipx.WebSocket.Enabled: false` to stop the reconnection attempts. Check whether the IPX web interface itself
   shows its WebSocket as connected.

## Development

```bash
dotnet build
dotnet test           # unit tests + integration tests against a fake IPX
dotnet run --project src/Ipx2Mqtt.Service
```

In VS Code, `F5` starts the service (configurations in [.vscode/launch.json](.vscode/launch.json)); it reads
`config/config.yml` and `config/entities.yml` from the repository. The "with .env" launch profile additionally loads
a `.env` file (copy [.env.example](.env.example) to `.env`, ignored by git) for environment overrides.

```
src/Ipx2Mqtt.Core      MQTT, discovery, availability, state diffing  (independent of the IPX, reusable)
src/Ipx2Mqtt.Ipx       IPX800 V5 client: WebSocket, REST, strategy, FakeIpxClient, entity mapping and discovery
src/Ipx2Mqtt.Service   worker, push endpoint, configuration files, Program.cs
tests/Ipx2Mqtt.Tests   xUnit: discovery, mapping, state diffing, parsing, IPX client against a fake server
```

To reuse the MQTT part in another adapter: reference `Ipx2Mqtt.Core`, call
`services.AddIpx2MqttCore(configuration)`, build your `ComponentDescriptor`s and publish with
`DiscoveryBuilder` / `DiscoveryPublisher` / `StatePublisher` / `AvailabilityManager`.

## Known limitations

- **The IPX WebSocket is not documented.** The service is built on observed messages
  (`{"_id": 65638, "on": false}` and `{"_id": …, "value": …}`). A firmware update may change this protocol.
  Safeguards: unknown messages are ignored (never blocking), a parsing error counter, an aggregated `Warning`,
  and **the REST polling takes over** while the WebSocket is unavailable.
- **Firmware version**: the documented API does not expose a firmware number. At startup the service logs what is
  available (`/api/system/ipx/info`: `apiVersion` and `objVersion`; this endpoint is missing on some firmwares) and
  adds it to the device card. To be completed if you know the endpoint that returns the firmware.
- **Commands over REST only**: the format of the commands sent over the WebSocket is not known.
  `PUT /api/core/io/{id}` is used with the documented body `{"on": true|false}`; dimmer levels go through
  `PUT /api/core/ana/{id}` with `{"value": n}`.
- **Automatic discovery relies on object names** (`[04-8R]Relay cmd 5`…): renaming those objects on the IPX changes
  what is discovered.
- Text objects (`/api/core/str`) are not read. Roller shutters (X-4VR) and thermostats are not mapped yet (see
  [Adding an extension](#adding-an-extension-or-a-component)).
- A short race window exists on reconnection: a change that happens while the complete snapshot is being read is
  applied afterwards by the WebSocket and prevails, which is the expected behaviour.
