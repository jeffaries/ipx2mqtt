# ipx2mqtt

Passerelle **IPX800 V5 → MQTT** pour **Home Assistant**, écrite en C# (.NET 10).

Le service lit l'état de l'IPX800 V5 (relais, entrées numériques, entrées analogiques, extensions EBX), le publie
sur un broker MQTT et déclare les entités à Home Assistant via la **découverte MQTT par appareil**
(*MQTT Discovery*). Pas d'intégration HACS, pas de Node-RED.

C'est le premier d'une série d'adaptateurs (Daikin, HomeWizard…) : toute la partie MQTT / Discovery /
disponibilité est isolée dans `Ipx2Mqtt.Core` et ne dépend pas de l'IPX.

> **Documentation de l'API IPX** : les fichiers OpenAPI officiels de GCE sont conservés dans
> [docs/ipx-api/](docs/ipx-api/) et le protocole utilisé par ce service (login chiffré, WebSocket, commandes)
> est résumé dans [docs/ipx-protocol.md](docs/ipx-protocol.md).

## Sommaire

- [Fonctionnement](#fonctionnement)
- [Démarrage rapide (Docker)](#démarrage-rapide-docker)
- [Configuration](#configuration)
- [Déclarer les entités](#déclarer-les-entités)
- [Topics MQTT](#topics-mqtt)
- [Variables d'environnement](#variables-denvironnement)
- [Objet PUSH sur l'IPX (optionnel)](#objet-push-sur-lipx-optionnel)
- [Dépannage avec MQTT Explorer](#dépannage-avec-mqtt-explorer)
- [Développement](#développement)
- [Limites connues](#limites-connues)

## Fonctionnement

```
                 WebSocket (source principale)
   IPX800 V5 ─────────────────────────────────┐
        │   REST /api/core/* (polling, plan B)│      ┌───────────┐        ┌────────────────┐
        │   REST PUT (commandes)              ├────► │ ipx2mqtt  │ ─────► │ Mosquitto      │ ◄──► Home Assistant
        └─────────────────────────────────────┘      └───────────┘  MQTT  └────────────────┘     (MQTT Discovery)
```

- **Source principale : le WebSocket de l'interface web de l'IPX.** Le client se connecte avec le même
  login chiffré que l'interface web (AES-ECB), se reconnecte automatiquement avec un *backoff* exponentiel plafonné,
  détecte une connexion morte (aucun message reçu pendant `ReceiveTimeoutSeconds`) et **resynchronise
  l'état complet à chaque (re)connexion**.
- **Plan B : polling REST** (`GET /api/core/io` et `/api/core/ana`). Il s'active automatiquement dès que le
  WebSocket est indisponible et se désactive dès qu'il revient.
- **Commandes** (`.../set`) : envoyées par REST (`PUT /api/core/io/{id}`, `PUT /api/core/ana/{id}`).
  L'état n'est publié **que lorsque l'IPX le confirme** (via le WebSocket ou le polling) : un ordre refusé ou
  perdu ne laisse jamais Home Assistant dans un état faux.
- **Disponibilité** (`ipx800/status`) : `offline` uniquement si **les deux sources** sont indisponibles
  (avec un court délai de grâce, `Ipx__OfflineGraceSeconds`, pour que la bascule WebSocket → REST ne fasse pas
  « clignoter » les entités). Dernière volonté MQTT (*Last Will*) : `offline`.
- **Seuls les changements** sont publiés, en *retained*.
- **Home Assistant redémarre** : le service s'abonne à `homeassistant/status` et, à la réception de `online`,
  republie la découverte (après un délai aléatoire de 1 à 5 s) puis tous les états courants.
- Le WebSocket n'étant **pas une API officielle**, un message inconnu ou mal formé est ignoré (log `Debug`),
  compté, et résumé dans un `Warning` agrégé : voir [Limites connues](#limites-connues).

## Démarrage rapide (Docker)

1. Repérez les identifiants `_id` des objets à exposer (voir [Déclarer les entités](#déclarer-les-entités)).
2. Éditez [docker-compose.yml](docker-compose.yml) : adresse de l'IPX (`Ipx__BaseUrl`), mot de passe
   (`Ipx__Password`) et entités.
3. Lancez :

```bash
docker compose up -d --build
docker compose logs -f ipx2mqtt
```

Au démarrage, les logs doivent indiquer : connexion au broker MQTT, `Logged in to the IPX`,
`IPX WebSocket connected`, `IPX full state resynchronised (N objects)`, puis `IPX is now available`.
Les entités apparaissent dans Home Assistant (*Paramètres → Appareils et services → MQTT*) sous
l'appareil « IPX800 V5 ».

> **Utilisateur IPX dédié** : créez sur l'IPX un utilisateur réservé à ce service (niveau « opérateur » ou
> supérieur pour pouvoir envoyer des commandes). Ne commitez jamais son mot de passe : utilisez une variable
> d'environnement ou un fichier `.env` non versionné.

## Configuration

La configuration vient de [appsettings.json](src/Ipx2Mqtt.Service/appsettings.json), surchargeable par des
[variables d'environnement](#variables-denvironnement) (séparateur `__`). Vous pouvez aussi monter votre propre
fichier : `-v ./appsettings.local.json:/app/appsettings.Production.json:ro`.

| Section | Clé | Défaut | Rôle |
|---|---|---|---|
| `Mqtt` | `Host`, `Port` | `localhost`, `1883` | Broker MQTT |
| | `Username`, `Password`, `UseTls` | — | Authentification / TLS |
| | `ClientId` | `ipx2mqtt` | Identifiant client (unique par instance) |
| | `BaseTopic` | `ipx800` | **Préfixe de tous les topics** |
| `Discovery` | `Prefix` | `homeassistant` | Préfixe de découverte de Home Assistant |
| | `StatusTopic` | `homeassistant/status` | Topic de naissance de Home Assistant |
| `Device` | `NodeId` | `ipx800` | Identifiant de l'appareil (lettres, chiffres, `_`, `-`) |
| | `Name`, `Manufacturer`, `Model` | `IPX800 V5`… | Fiche de l'appareil dans Home Assistant |
| `Ipx` | `BaseUrl` | `http://192.168.1.54` | URL de l'IPX (`https://` → `wss://` automatiquement) |
| | `Login`, `Password` | `api`, — | Utilisateur IPX (login chiffré, requis pour le WebSocket) |
| | `ApiKey` | — | Clé API officielle (optionnelle) : si renseignée, les appels REST l'utilisent |
| | `RequestTimeoutSeconds` | `10` | Délai des requêtes REST |
| | `AutoDiscover` | `true` | Exposer automatiquement relais, variateurs et sorties analogiques |
| | `OfflineGraceSeconds` | `5` | Délai avant de déclarer l'IPX hors ligne |
| | `AllowInvalidCertificate` | `false` | Accepter un certificat auto-signé |
| `Ipx:WebSocket` | `Enabled` | `true` | Désactiver pour n'utiliser que le polling |
| | `ReceiveTimeoutSeconds` | `30` | Silence toléré avant de considérer la connexion morte |
| | `KeepAliveSeconds` | `15` | Keep-alive WebSocket |
| | `ReconnectMinSeconds` / `ReconnectMaxSeconds` | `1` / `60` | Bornes du *backoff* exponentiel |
| `Ipx:Polling` | `IntervalSeconds` | `10` | Période du polling (plan B, actif seulement si le WebSocket est tombé) |
| `Push` | `Enabled`, `Port`, `Token` | `false`, `8080` | [Endpoint push](#objet-push-sur-lipx-optionnel) |

L'IPX publie son horloge chaque seconde sur le WebSocket ; c'est ce qui permet de détecter une connexion
morte rapidement. Les objets non déclarés dans `Entities` (horloge, diagnostics…) sont ignorés dès leur arrivée.

## Déclarer les entités

### Découverte automatique (par défaut)

Au démarrage, le service lit la liste des objets de l'IPX (`/api/core/io`, `/api/core/ana`) et expose **tous** les
relais, variateurs et sorties analogiques trouvés, sans rien déclarer. L'IPX ne renvoie pas les `_id` dans la
description de ses extensions : ils sont déduits des **noms d'objets**, qui suivent des motifs fixes.

| Objets IPX (nom) | Entité Home Assistant | Type / index (topics) |
|---|---|---|
| `[01-IPX]Relay cmd n` + `Relay state n` | `switch` | `relay/n` |
| `[IPX]Open coll. n` + `Open coll. state n` | `switch` | `collector/n` |
| `[03-8R]Relay cmd n` + `Relay state n` (X-8R) | `switch` | `03-8r/n` |
| `[06-DM]On/Off n` + `Consign n` + `Position n` (variateur) | `light` (marche + niveau 0-100) | `06-dm/n` |
| `[06-DM]On/Off All` | `switch` (tout) | `06-dm-all/1` |
| Objets `ana` liés à un X-010V, X-PWM ou X-400 | `number` (0-100) | `x010v`, `xpwm`, `x400` |

Les entités déclarées dans `Entities` (ci-dessous) **restent prioritaires** : une entité découverte qui utilise les
mêmes identifiants IPX est ignorée. Si l'IPX est injoignable au démarrage, le service réessaie toutes les 5 s
(la découverte MQTT n'est publiée qu'une fois la liste complète). Désactivation : `Ipx__AutoDiscover=false`.
Le log de démarrage indique « N declared + M auto-discovered entities ».

> Les sorties X-010V / X-PWM / X-400 n'ont pas pu être validées (absentes de l'IPX de test).

### Libellés des entités découvertes : `config/ipx2mqtt.json`

Les noms affichés dans Home Assistant se règlent dans un fichier **au format `appsettings.json`**, hors de
l'application : [config/ipx2mqtt.json](config/ipx2mqtt.json). La clé est le code module `<module>.<canal>`
(`01-IPX.03`, `03-8R.02`, `04-8R.05`, `06-DM.01`…), c'est-à-dire le nom de l'objet IPX (`[04-8R]Relay cmd 5`).

```json
{ "Ipx": { "Labels": { "04-8R.05": "Lumières Z3", "06-DM.01": "Lumières Salle à manger Z1" } } }
```

Un canal sans libellé garde un nom générique (« 03-8R relais 3 »). Le fichier peut contenir n'importe quel autre
réglage (par exemple `Entities`) ; il surcharge `appsettings.json`, et les variables d'environnement restent
prioritaires. Emplacement, premier trouvé :

1. la variable d'environnement `IPX2MQTT_CONFIG` (chemin complet ; erreur s'il n'existe pas) ;
2. `/config/ipx2mqtt.json` : **dans Docker**, montez le dossier avec `./config:/config:ro` (déjà dans
   [docker-compose.yml](docker-compose.yml)) ;
3. `config/ipx2mqtt.json` dans le dossier courant ou l'un de ses parents (développement : F5 sous VS Code).

Le log de démarrage indique « Extra settings loaded from … ». Après une modification, redémarrez le service ;
si un nom change, Home Assistant met à jour l'entité (l'`unique_id` ne change pas).

### Déclaration manuelle

La liste des entités exposées vient aussi de la configuration (`Entities`).

```json
"Entities": [
  { "Type": "relay",  "Index": 1, "Name": "Éclairage garage", "Component": "switch",
    "StateId": 65544, "CommandId": 65536 },
  { "Type": "input",  "Index": 1, "Name": "Porte garage", "Component": "binary_sensor",
    "StateId": 65552, "DeviceClass": "door" },
  { "Type": "analog", "Index": 1, "Name": "Température cave", "Component": "sensor",
    "StateId": 262153, "Unit": "°C", "DeviceClass": "temperature", "StateClass": "measurement", "Decimals": 1 }
]
```

### Trouver les `_id`

Sur l'IPX V5, **chaque objet a un identifiant numérique `_id`**, et la commande et l'état d'un relais sont
**deux objets distincts** :

| Objet IPX | `_id` (exemple) |
|---|---|
| `[01-IPX]Relay cmd 1` (commande) | 65536 |
| `[01-IPX]Relay state 1` (état) | 65544 |
| `[01-IPX]Digital input 1` | 65552 |
| `[IPX]Analog input 1` | 262144 |

Pour lister tous les objets avec leur nom (remplacez l'hôte et le jeton ; ou utilisez une clé API avec
`?ApiKey=...`) :

```bash
curl "http://IPX/api/core/io?AuthToken=<jeton>"    # relais, entrées, objets logiques (champ "on")
curl "http://IPX/api/core/ana?AuthToken=<jeton>"   # valeurs analogiques (champ "value")
```

Plages d'identifiants : `io` 65536–131071, `ana` 196608–393215, `str` 524288–851967.

### Champs d'une entité

| Champ | Obligatoire | Description |
|---|---|---|
| `Type` | oui | Libellé utilisé dans les topics (`relay`, `input`, `analog`, `dimmer`…) |
| `Index` | non | Numéro dans les topics. Par défaut : `StateId` (ou `CommandId`) |
| `Name` | oui | Nom affiché dans Home Assistant |
| `Component` | oui | `switch`, `binary_sensor`, `sensor`, `light` |
| `StateId` | selon le composant | Objet IPX portant l'état |
| `CommandId` | `switch`, `light` | Objet IPX à écrire pour commander |
| `DeviceClass`, `Icon` | non | Classe d'appareil / icône Home Assistant |
| `Unit`, `StateClass`, `Decimals` | non | `sensor` : unité, classe d'état, nombre de décimales publiées |
| `Invert` | non | `binary_sensor` : inverse ON/OFF |
| `BrightnessStateId`, `BrightnessCommandId` | non | `light` : objets analogiques (0–100) du niveau |

### Composants pris en charge

| `Component` | Usage | Exigences |
|---|---|---|
| `switch` | Relais de l'IPX, de l'X-8R… | `CommandId` (et `StateId`, sinon l'état est lu sur `CommandId`) |
| `binary_sensor` | Entrées numériques, états | `StateId` |
| `sensor` | Entrées analogiques, mesures | `StateId` |
| `number` | Curseurs : niveau de variateur, sorties analogiques | `CommandId` ; `Min` / `Max` (défaut 0 / 100) |
| `light` | Variateurs (X-DIMMER, X-DM) | `CommandId` ; `BrightnessCommandId` / `BrightnessStateId` pour le niveau |

Exemple de variateur (`[06-DM]On/Off 1` = io 65587, `[06-DM]Consign 1` = ana 262194, `[06-DM]Position 1` = ana 196617) :

```json
{ "Type": "dimmer", "Index": 1, "Name": "Salon", "Component": "light",
  "StateId": 65587, "CommandId": 65587, "BrightnessCommandId": 262194, "BrightnessStateId": 196617 }
```

### Ajouter une extension ou un composant

Chaque plateforme Home Assistant est un petit `IComponentMapper`
([ComponentMappers.cs](src/Ipx2Mqtt.Ipx/Mapping/ComponentMappers.cs)) enregistré dans
[IpxServiceCollectionExtensions.cs](src/Ipx2Mqtt.Ipx/IpxServiceCollectionExtensions.cs). Pour un volet roulant
X-4VR (`cover`), il suffit d'ajouter un mapper qui associe les objets `ioCommandUp/Down/Stop` et
`anaPosition/anaCommand` : le reste (WebSocket, MQTT, découverte, diff d'états) ne change pas.

## Topics MQTT

Avec le préfixe par défaut `ipx800` :

| Topic | Sens | Contenu |
|---|---|---|
| `ipx800/status` | publié, *retained* | `online` / `offline` (Last Will : `offline`) |
| `ipx800/<type>/<index>/state` | publié, *retained* | `ON`/`OFF` ou valeur numérique (ex. `ipx800/relay/1/state`) |
| `ipx800/<type>/<index>/set` | souscrit | `ON` / `OFF` |
| `ipx800/<type>/<index>/brightness/state` | publié, *retained* | niveau 0–100 (`light`) |
| `ipx800/<type>/<index>/brightness/set` | souscrit | niveau 0–100 (`light`) |
| `homeassistant/device/<node_id>/config` | publié, *retained* | découverte par appareil |
| `homeassistant/status` | souscrit | `online` → republication de la découverte puis des états |

La découverte est **un seul message** par appareil (`dev`, `o`, `cmps`). Les topics d'état et de commande sont
déclarés **dans chaque composant** (les options de la racine s'appliqueraient à tous les composants) ; seule la
disponibilité est partagée à la racine.

**Supprimer proprement les entités** : publiez un message *retained* **vide** sur le topic de découverte :

```bash
mosquitto_pub -h <broker> -t homeassistant/device/ipx800/config -r -n
```

## Variables d'environnement

Toute clé de configuration se surcharge avec `Section__Cle` (deux soulignés). Les listes utilisent l'index :

| Variable | Exemple |
|---|---|
| `Mqtt__Host` | `mosquitto` |
| `Mqtt__Username` / `Mqtt__Password` | `ha` / `secret` |
| `Mqtt__BaseTopic` | `maison/ipx` |
| `Ipx__BaseUrl` | `http://192.168.1.54` |
| `Ipx__Login` / `Ipx__Password` | `api` / `secret` |
| `Ipx__ApiKey` | clé API officielle (optionnelle) |
| `Ipx__WebSocket__Enabled` | `false` pour forcer le polling |
| `Ipx__Polling__IntervalSeconds` | `5` |
| `Device__NodeId` | `ipx800` |
| `Entities__0__Type` / `Entities__0__Name` / `Entities__0__Component` | `relay` / `Garage` / `switch` |
| `Entities__0__StateId` / `Entities__0__CommandId` | `65544` / `65536` |
| `Logging__LogLevel__Default` | `Debug` pour voir chaque message ignoré et chaque changement |

## Objet PUSH sur l'IPX (optionnel)

Le WebSocket suffit normalement. L'endpoint *push* est une **source complémentaire** : l'objet **Push** de l'IPX
envoie une requête HTTP au service à chaque changement d'un objet que vous lui associez.

> ⚠️ **Fonctionnalité non validée sur matériel réel.** Le format exact de la requête envoyée par l'objet Push
> dépend de sa configuration ; les deux formes ci-dessous sont celles que le service sait lire.

1. Activez l'endpoint : `Push__Enabled=true`, `Push__Port=8080` (publiez le port dans Docker) et, de préférence,
   `Push__Token=<secret>`.
2. Sur l'IPX, créez un objet **Push** (*Objets → Push*) :
   - **Adresse** : l'adresse IP de la machine qui héberge ipx2mqtt, **port** `8080`, méthode `GET`.
   - **URI ON** : `/push/65544/on?token=<secret>` · **URI OFF** : `/push/65544/off?token=<secret>`
     (`65544` = `_id` de l'objet d'état, ici l'état du relais 1).
   - Liez l'entrée **On** du Push à l'objet dont vous voulez suivre les changements.
3. Pour un envoi JSON, utilisez `POST /push` avec un corps `{"_id": 65544, "on": true}` ou
   `{"_id": 262153, "value": 9.2}`, et le jeton dans l'en-tête `X-Push-Token`.

| Requête | Effet |
|---|---|
| `GET\|POST\|PUT /push/{id}/{valeur}` | `valeur` = `on`, `off`, `true`, `false` ou un nombre |
| `POST /push` | corps JSON `{"_id":…, "on":…}` ou `{"_id":…, "value":…}` |

## Dépannage avec MQTT Explorer

Avec [MQTT Explorer](https://mqtt-explorer.com/), connectez-vous au broker et regardez l'arborescence :

1. **`ipx800/status`**
   - `online` : tout fonctionne.
   - `offline` : l'IPX est injoignable par **les deux** sources (ou le service est arrêté). Consultez les logs :
     `docker compose logs ipx2mqtt`. Vérifiez `Ipx__BaseUrl`, le login et le mot de passe
     (`IPX secure login failed` = identifiants refusés).
   - absent : le service n'est jamais connecté au broker (`Mqtt__Host`, identifiants MQTT).
2. **`homeassistant/device/<node_id>/config`** : le JSON de découverte doit exister (case *retained*). S'il manque,
   les entités n'apparaîtront pas dans Home Assistant. Vérifiez que `homeassistant/status` est bien le topic de
   naissance configuré dans l'intégration MQTT de Home Assistant.
3. **`ipx800/<type>/<index>/state`** : valeurs `ON`/`OFF` ou numériques. Si un topic manque, l'`_id` configuré est
   probablement faux (les objets non déclarés sont ignorés). Passez les logs en `Debug` pour voir les changements.
4. **Tester une commande** : publiez `ON` sur `ipx800/relay/1/set`. Le log doit afficher
   `Command sent to IPX object 65536`, puis l'état doit passer à `ON` **quand l'IPX le confirme**. Si l'état ne
   change pas : l'utilisateur IPX n'a probablement pas le droit d'écrire (erreur `403`) ou la commande a été
   refusée (voir le log).
5. **Entités « indisponibles » dans Home Assistant** : `ipx800/status` vaut `offline`, ou le préfixe `BaseTopic`
   diffère entre la découverte et les topics réels.
6. **Entités en double après un changement de `Index`, `Type` ou `NodeId`** : supprimez l'ancienne découverte
   (message *retained* vide, voir plus haut).
7. **Messages ignorés** : un `Warning` « *N IPX message(s) could not be parsed* » signale un format de message
   inattendu (voir [Limites connues](#limites-connues)). Passez en `Debug` pour voir chaque message ignoré.

## Développement

```bash
dotnet build
dotnet test           # tests unitaires + tests d'intégration contre un faux IPX
dotnet run --project src/Ipx2Mqtt.Service
```

Dans VS Code, `F5` lance le service (configurations dans [.vscode/launch.json](.vscode/launch.json)) : copiez
[.env.example](.env.example) vers `.env` (ignoré par git) et renseignez l'IPX et le broker.

```
src/Ipx2Mqtt.Core      MQTT, découverte, disponibilité, diff d'états  (indépendant de l'IPX, réutilisable)
src/Ipx2Mqtt.Ipx       client IPX800 V5 : WebSocket, REST, stratégie, FakeIpxClient, mapping des entités
src/Ipx2Mqtt.Service   worker, endpoint push, configuration, Program.cs
tests/Ipx2Mqtt.Tests   xUnit : découverte, mapping, diff d'états, parsing, client IPX contre un faux serveur
```

Pour réutiliser la partie MQTT dans un autre adaptateur : référencez `Ipx2Mqtt.Core`, appelez
`services.AddIpx2MqttCore(configuration)`, construisez vos `ComponentDescriptor` et publiez avec
`DiscoveryBuilder` / `DiscoveryPublisher` / `StatePublisher` / `AvailabilityManager`.

## Limites connues

- **Le WebSocket de l'IPX n'est pas documenté.** Le service est construit sur les messages observés
  (`{"_id": 65638, "on": false}` et `{"_id": …, "value": …}`). Une mise à jour du firmware peut changer ce
  protocole. Les parades : message inconnu ignoré (jamais bloquant), compteur d'erreurs de parsing, `Warning`
  agrégé, et **le polling REST prend le relais** tant que le WebSocket est indisponible.
- **Version du firmware** : l'API documentée n'expose pas de numéro de firmware. Le service journalise au
  démarrage ce qui est disponible (`/api/system/ipx/info` : `apiVersion` et `objVersion`) et l'ajoute à la fiche
  de l'appareil. À compléter si vous connaissez l'endpoint qui renvoie le firmware.
- **Commandes par REST uniquement** : le format des commandes envoyées par le WebSocket n'est pas connu.
  `PUT /api/core/io/{id}` est utilisé avec le corps documenté `{"on": true|false}` ; les niveaux de variateur
  passent par `PUT /api/core/ana/{id}` avec `{"value": n}`.
- Les objets de type texte (`/api/core/str`) ne sont pas lus. Les volets roulants (X-4VR) et les thermostats ne sont
  pas encore mappés (voir [Ajouter une extension](#ajouter-une-extension-ou-un-composant)).
- Une courte fenêtre de course existe à la reconnexion : un changement survenu pendant la lecture de l'instantané
  complet est appliqué ensuite par le WebSocket et prévaut, ce qui est le comportement attendu.
