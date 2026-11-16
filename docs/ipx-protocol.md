# Protocole IPX800 V5 utilisé par ipx2mqtt

Résumé de ce que le service utilise, avec ce qui est **documenté** (OpenAPI GCE, dossier [ipx-api/](ipx-api/))
et ce qui a été **observé** sur un IPX réel (WebSocket de l'interface web). Aucun secret dans ce fichier.

## Documentation officielle (OpenAPI)

| Fichier | Contenu |
|---|---|
| [ipx-api/ipx800v5_api_core.json](ipx-api/ipx800v5_api_core.json) | `/api/core/io`, `/ana`, `/str` (objets de base) |
| [ipx-api/ipx800v5_api_ipx.json](ipx-api/ipx800v5_api_ipx.json) | `/api/system/ipx` (carte IPX, `/info`) |
| [ipx-api/ipx800v5_api_extension.json](ipx-api/ipx800v5_api_extension.json) | Extensions EBX : X-8R, X-DIMMER, X-4VR, X-THL, X-4FP… |
| [ipx-api/ipx800v5_api_object.json](ipx-api/ipx800v5_api_object.json) | Objets : thermostat, timer, push, mqtt, planning… |
| [ipx-api/ipx800v5_api_plugin.json](ipx-api/ipx800v5_api_plugin.json) | Plugins : météo, IPX800 V4, X-POOL |

Authentification documentée : `?ApiKey=<clé>` (niveaux 0 lecture, 1 utilisateur) ou `?AuthToken=<jeton>`
(niveaux 2 à 4, obtenu par login).

## Login sécurisé (observé, utilisé par l'interface web)

1. `POST /api/auth/secure/{login}` (corps vide) → `{"secureKey": "<16 caractères>"}`
2. Chiffrer `{"login":"<login>","passwd":"<mot de passe>"}` en **AES-ECB / PKCS7**, clé = octets UTF-8 de
   `secureKey` (AES-128), résultat en Base64.
3. `POST /api/auth/login/{login}?secure=true` avec `{"secureContent":"<base64>"}`
   → `{"Status":"Succes","AuthToken":"<jeton>","userObj":{...}}` (la casse `AuthToken` est exacte).

Implémentation : [IpxCrypto.cs](../src/Ipx2Mqtt.Ipx/IpxCrypto.cs), [IpxHttpApi.cs](../src/Ipx2Mqtt.Ipx/IpxHttpApi.cs).

## Lecture de l'état

- `GET /api/core/io` → `[{"_id":65544,"name":"...","link0":0,"link1":0,"virtual":false,"on":true}, ...]`
- `GET /api/core/ana` → `[{"_id":262153,"name":"...","unit":"°C","nbdecimal":2,"virtual":false,"value":9.19}, ...]`
- `GET /api/core/str` → `[{"_id":524288,"name":"...","value":"..."}, ...]` (non lu par le service)
- `GET /api/system/ipx/info` → `{"apiVersion":"1.6","objVersion":1}` (pas de numéro de firmware documenté)

Plages d'`_id` : `io` 65536–131071, `ana` 196608–393215, `str` 524288–851967.

Sur un IPX V5, **commande et état d'un relais sont deux objets distincts** (ex. `Relay cmd 1` = 65536,
`Relay state 1` = 65544).

## WebSocket (observé, non documenté)

- URL : `ws://<hôte>/websocket/dashboard/open?AuthToken=<jeton>` (`wss://` si l'IPX est en https).
- Messages texte JSON, un objet par message : `{"_id":65638,"on":false}` ou `{"_id":196618,"value":33}`.
- L'objet `_id` 327687 (`[IPX]CLOCK`) est émis chaque seconde ; les `_id` 327680–327686 sont des diagnostics.
  Les changements de **commande** (ex. `Relay cmd 8`) remontent aussi.
- Aucun format de commande par WebSocket n'est connu : les commandes passent par REST.

Implémentation : [IpxWebSocketClient.cs](../src/Ipx2Mqtt.Ipx/IpxWebSocketClient.cs),
[IpxStateParser.cs](../src/Ipx2Mqtt.Ipx/IpxStateParser.cs).

## Commandes

| Cible | Requête | Corps | Source |
|---|---|---|---|
| io (relais, on/off) | `PUT /api/core/io/{id}` | `{"on": true}` / `{"on": false}` | documenté |
| io (inversion) | `PUT /api/core/io/{id}` | `{"toggle": true}` | observé (interface web) ; non utilisé |
| ana (consigne) | `PUT /api/core/ana/{id}` | `{"value": 40}` | documenté (schéma `object_ana`) |

L'interface web ajoute `&option=async` à ses requêtes ; le service ne l'utilise pas.

## Extensions utiles (documentation `extension`)

- **X-8R** (`/api/ebx/x8r`) : `ioOutput` (commande), `ioOutputState` (état), `ioLongPush`.
- **X-DIMMER** (`/api/ebx/xdimmer`) : `ioOn`, `anaCommand` (niveau demandé 0–100), `anaPosition` (niveau réel).
- **X-4VR** (`/api/ebx/x4vr`) : `ioCommandUp/Down/Stop`, `anaCommand` (position demandée), `anaPosition`.
- **X-THL** (`/api/ebx/xthl`) : `anaTemp`, `anaHum`, `anaLum`.

Les identifiants de ces dépendances s'obtiennent avec `GET /api/ebx/<extension>` (champs `*_id`).
