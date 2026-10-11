# AirPlay screen-mirroring RTSP control channel — implementation spec

Extracted from UxPlay 1.74-master at
`C:\Users\Administrator\AppData\Local\Temp\airplay-ref\uxplay\UxPlay-master`.
Citations are `file:line`. Anything not derivable from that source is marked
**[not determinable from source]**.

## 0. Architecture

- One **TCP listening socket** serves the whole RTSP/HTTP control channel
  (`httpd.c:669-719`, IPv4 + IPv6, `backlog 5`). Dispatch is on
  `(protocol, method, url)` in `raop.c:192-546` (`conn_request`).
- Max 12 connections (`httpd.c:157`); a connection is dropped after 60 s of
  silence (`httpd.c:377`, `473-477`).
- Parsing: llhttp (`http_request.c:227` `llhttp_init(..., HTTP_REQUEST, ...)`).
  The vendored copy accepts `HTTP/1.1` and `RTSP/1.0` (`llhttp.c:3825`
  `"Expected HTTP/, RTSP/ or ICE/"`, `llhttp.h:372-386` RTSP method map).
- The protocol string must compare equal to `"RTSP/1.0"` or `"HTTP/1.1"`
  exactly (`raop.c:406`, `raop.c:445`). It is assembled as 4 chars + `/` +
  3 chars (`http_request.c:65-94`, `242`).

## 1. Endpoint table

### 1.1 protocol == "RTSP/1.0" (`raop.c:406-444`)

| Method | Path test (literal) | Handler | Purpose | Needed for iOS screen mirroring? |
|---|---|---|---|---|
| OPTIONS | URL ignored (`raop.c:427-428`) | `raop_handler_options` `raop_handlers.h:591-597` | 200 + `Public: SETUP, RECORD, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER` | YES (first request) |
| GET | `strstr(url,"/info")` (`raop.c:424`) | `raop_handler_info` `raop_handlers.h:36-249` | capability/key discovery (binary plist) | YES |
| POST | `/pair-setup` | `raop_handlers.h:435-461` | returns 32-byte Ed25519 public key | conditional, see §4 |
| POST | `/pair-verify` | `raop_handlers.h:463-542` | X25519 ECDH + Ed25519 signature, 2 round trips | conditional, see §4 |
| POST | `/fp-setup` | `raop_handlers.h:544-589` | FairPlay 3: 16 B challenge -> 142 B; 164 B handshake -> 32 B | YES |
| SETUP | URL ignored (`raop.c:429-431`) | `raop_handlers.h:599-1067` | session + stream setup | YES |
| RECORD | URL ignored | `raop_handlers.h:1217-1229` | ack; `Audio-Latency`, `Audio-Jack-Status` | YES |
| SET_PARAMETER | URL ignored | `raop_handlers.h:1125-1181` | volume / progress / coverart / metadata | volume yes |
| GET_PARAMETER | URL ignored | `raop_handlers.h:1069-1123` | volume query / keep-alive | keep-alive yes |
| POST | `/feedback` | `raop_handlers.h:1206-1215` | heartbeat, no-op | YES-ish |
| TEARDOWN | URL ignored | `raop_handlers.h:1254-1325` | stop session | YES |
| FLUSH | URL ignored | `raop_handlers.h:1231-1252` | audio flush (`RTP-Info: seq=`) | NO (audio only) |
| POST | `/audioMode` | `raop_handlers.h:1183-1204` | AirPlay 2 audio mode, unhandled | NO |
| POST | `/pair-pin-start` | `raop_handlers.h:251-274` | PIN announcement | NO |
| POST | `/pair-setup-pin` | `raop_handlers.h:276-433` | SRP-6a PIN pairing (binary plists) | NO (only `-pin`/`-pw`) |
| any other method | — | `raop.c:442-444` | `501 Not Implemented` | — |

### 1.2 protocol == "HTTP/1.1" (`raop.c:445-475`) — AirPlay *video*/HLS + AirPlay 2

`POST /reverse`, `POST /play`, `POST /getProperty?…`, `POST /scrub?…`,
`POST /rate?…`, `POST /stop`, `POST /action`, `POST /fp-setup2`,
`GET /server-info`, `GET /playback-info`, `PUT /setProperty?…`.
**None is required for iOS screen mirroring**; they belong to the HLS/video path
that UxPlay only enables with `-hls` (`raop.c:262-266`).
`GET /server-info` and `GET /playback-info` are *not* `GET /info` (see §3).

### 1.3 HLS channel

Any request with `Host`, without `CSeq` and without `X-Apple-Session-ID`
(`raop.c:268-270`, `476-477`) goes to `http_handler_hls`
(`http_handlers.h:922-993`): `GET /master.m3u8` and media playlists.
Not mirroring.

### 1.4 Endpoints that do not exist here

`/command`, `/stream.xml`, `/stats`, `/audio`, `/video` — absent (exhaustive
grep). `/event` is not inbound: it is the reverse request the receiver *sends*
over the PTTH socket (`fcup_request.h:86`
`http_response_reverse_request_init(request, "POST", "/event", "HTTP/1.1")`).

## 2. Framing

- Request line `<METHOD> <URL> <PROTOCOL>/1.0 CRLF`; URL taken verbatim
  (leading `/`); `OPTIONS *` works because OPTIONS ignores the URL.
- **`CSeq` is mandatory** for RTSP requests: `raop.c:263-266`
  `if (!cseq && !raop->hls_support && !ble) { log; return; }` -> **no response at
  all** (`httpd.c:626-628` logs "httpd didn't get response"). `CSeq` is parsed as
  a non-negative int; invalid -> request dropped without response
  (`raop.c:241-253`); echoed verbatim as `CSeq: <n>` (`raop.c:490-492`).
- Response first line: **the same protocol string as the request** + code +
  message (`raop.c:335`, `http_response.c:72-91`). Handlers that override:
  `RTSP/1.0 470 Client Authentication Failure` (`raop_handlers.h:432`),
  `RTSP/1.0 501 Not Implemented` (`:558`), `RTSP/1.0 451 Parameter not
  understood` (`:1081`,`:1137`), `RTSP/1.0 401 Unauthorized` (`:744`),
  `HTTP/1.1 421 Misdirected Request` (`http_handlers.h:235`),
  `HTTP/1.1 400 Bad Request` (`:675`,`:909`), `HTTP/1.1 404 Not Found`
  (`:928`), `HTTP/1.1 101 Switching Protocols` (`:385`), and
  `431 Request Header Fields Too Large` (`raop.c:237`).
- Header serialization `Name: value\r\n` (`http_response.c:118-128`).
  Emission order: status line -> `Audio-Jack-Status` (added *before* dispatch,
  `raop.c:338-340`) -> handler headers -> `Server` -> `CSeq` ->
  `Content-Length` -> CRLF -> body.
- `Server: AirTunes/220.68` on every non-HLS response (`raop.c:489`;
  `GLOBAL_VERSION` = `"220.68"`, `global.h:22`).
- `Audio-Jack-Status: connected; type=digital` on every RTSP request that has a
  `CSeq` **except RECORD** (`raop.c:338-340`); RECORD gets
  `Audio-Jack-Status: connected; type=analog` (`raop_handlers.h:1228`).
- No `Apple-*` headers are emitted or required. There is **no**
  `Apple-Challenge`/`Apple-Response` RSA path.
- Body delimitation: requests by `Content-Length` (llhttp); responses by
  `Content-Length` in `http_response_finish` (`http_response.c:131-170`):
  body -> `Content-Length: N\r\n\r\n` + body; no body but a `Content-Type`
  header present -> `Content-Length: 0\r\n\r\n`; no body and no `Content-Type`
  -> bare `\r\n` (no `Content-Length`). Recommendation: always emit
  `Content-Length`.
- `Content-Type` values used: `application/x-apple-binary-plist` (**the** binary
  plist type, `raop_handlers.h:248`), `text/x-apple-plist+xml` (XML plist, HTTP
  channel only, `http_handlers.h:105`,`203`,`350`), `application/octet-stream`
  (pairing/FairPlay), `text/parameters` (volume/progress),
  `application/x-mpegURL; charset=utf-8`, `image/jpeg`, `image/png`,
  `application/x-dmap-tagged`.
- Header limits: >20 fields, field name >64, value >1024 -> `431`
  (`raop.c:225-239`).
- Reverse HTTP: `POST /reverse` -> `101` + `Connection: Upgrade` +
  `Upgrade: PTTH/1.0` (`http_handlers.h:380-387`); the socket is retyped
  `CONNECTION_TYPE_PTTH`, and inbound data whose first 8 bytes are `HTTP/1.1` or
  `EVENT/1.0` is not parsed (`httpd.c:510-540`, `559-572`).

## 3. GET /info (RTSP)

Three request forms (`raop_handlers.h:44-47`): (1) CSeq + a binary-plist body /
Content-Type `application/x-apple-binary-plist`; (2) CSeq, no body; (3) no CSeq
(BLE discovery; URL contains `txtAirPlay`/`txtRAOP`).

Response is always a **binary plist** (`plist_to_bin`, `:246`) with
`Content-Type: application/x-apple-binary-plist` (`:248`).

**Branch A — request has any `Content-Type` header** (`:109-111` -> `finished`):
the dict contains only `txtAirPlay` and/or `txtRAOP` (data = raw mDNS TXT bytes),
selected by the plist `qualifier[0]` string (`:73-85`) or, if `CSeq` is absent,
by substring match in the URL (`:91-94`). Nothing else. Unrecognized qualifier ->
empty dict.

**Branch B — no Content-Type, CSeq present (the iOS mirroring probe):**

| key | plist type | value / rule | line |
|---|---|---|---|
| `deviceID` | string | AirPlay MAC `aa:bb:cc:dd:ee:ff` (`utils.c:117-140`) | 119-120 |
| `macAddress` | string | same string | 122-123 |
| `pk` | data (32 B) | receiver Ed25519 public key, hex-decoded | 126-131 |
| `features` | uint (64-bit) | `(features2<<32)|features1` = **0x5A7FFEE6** | 133-135 |
| `name` | string | mDNS service name | 137-140 |
| `pi` | string | **"2e388006-13ba-4041-9a67-25dd4a43d536"** | 142-143 |
| `vv` | uint | **2** | 145-146 |
| `statusFlags` | uint | **68** | 148-149 |
| `keepAliveLowPower` | uint | **1** | 151-152 |
| `sourceVersion` | string | **"220.68"** | 154-155 |
| `keepAliveSendStatsAsBody` | bool | **true** | 157-158 |
| `model` | string | **"AppleTV3,2"** | 160-161 |
| `initialVolume` | real | current volume, dB (0.0 … -30.0) | 168-169 |
| `audioLatencies` | array[2] | `{type:100(uint), inputLatencyMicros:0(uint), audioType:"default", outputLatencyMicros:false(bool)}`, then `type:101` | 171-193 |
| `audioFormats` | array[2] | `{type:100(uint), audioInputFormats:0x3fffffc, audioOutputFormats:0x3fffffc}`, then `type:101` | 195-213 |
| `displays` | array[1] | see below | 215-243 |

`displays[0]`: `uuid` = `"e0ff8a27-6738-3d56-8a16-cc53aacee925"` (string),
`widthPhysical` 0 (uint), `heightPhysical` 0 (uint), `width` 1920 (uint),
`height` 1080 (uint), `widthPixels` 1920, `heightPixels` 1080 (both =
config width/height, `raop.c:620-621`), `rotation` false (bool),
`refreshRate` = `1.0/refreshRate` (real; default 60 -> 0.0166666; the comment
claiming "0.166666" is wrong), `maxFPS` 30 (uint), `overscanned` false (bool),
`features` 14 (uint). Note the type quirks: `outputLatencyMicros`,
`rotation`, `overscanned` are booleans.

**Branch C — no Content-Type, no CSeq (BLE):** as B but stops after `model`
(`:163-166`): no `initialVolume`/`audioLatencies`/`audioFormats`/`displays`.

Constants: `FEATURES_1 "0x5A7FFEE6"`, `FEATURES_2 "0x0"` (`dnssdint.h:32-34`) ->
`features` = 0x5A7FFEE6. `pk` must equal the mDNS TXT `pk`
(`raop.c:816-820` uses the same `pk_str`).

Contrast (do not confuse): HTTP/1.1 `GET /server-info`
(`http_handlers.h:46-107`) returns an **XML** plist
(`text/x-apple-plist+xml`) with `features` = **0x27F**, `macAddress`, `model`,
`osBuildVersion` = `"12B435"`, `protovers` = `"1.0"`, `srcvers` = `"220.68"`,
`vv` = 2, `deviceid` (lowercase key), body keeps its trailing `'\n'`
(`http_handlers.h:99-102`). HLS channel, not mirroring.

## 4. Pairing (the critical question)

No TLV8 anywhere in the tree (grep `tlv` -> 0 hits).

### 4.1 `POST /pair-setup` — 1 round trip, raw 32 bytes

`raop_handlers.h:435-461`.
- Request body must be exactly 32 bytes (`:446-450`); contents and Content-Type
  are ignored. Wrong length -> bare `200 OK`, no body.
- Response = 32 raw bytes, the receiver's persistent **Ed25519 public key**
  (`:452`, `pairing.c:101-106`), `Content-Type: application/octet-stream`
  (`:457`); session status set to `STATUS_SETUP` (`:453`,
  `pairing.c:141-146`). No PIN, no SRP, no proof, no client check.

### 4.2 `POST /pair-verify` — 2 round trips, 4-byte state prefix + raw keys

`raop_handlers.h:463-542`.
- Gate (`:470-477`): if the session status is not SETUP/HANDSHAKE *and*
  `use_pin` is false, the handler returns immediately -> bare `200 OK`, no body.
  So without `-pin`, **`/pair-verify` is only honoured after `/pair-setup`**.
- Round 1 (`:489-524`): request = 4-byte header with `body[0] == 1` + 32-byte
  X25519 client public key + 32-byte Ed25519 client public key (total 68;
  `:490`); bytes 1-3 are never validated. Response body = 32-byte X25519 server
  public key + 64-byte Ed25519 signature = **96 bytes**,
  `Content-Type: application/octet-stream`.
  - `ecdh_secret = X25519(server_priv, client_pub)` (`pairing.c:161-180`).
  - Signature plaintext = `Ed25519_sign(server_ecdh_pub || client_ecdh_pub)`
    (`pairing.c:390-408`), then AES-128-CTR encrypted with
    key `SHA512("Pair-Verify-AES-Key" || ecdh_secret)[0:16]`, IV
    `SHA512("Pair-Verify-AES-IV" || ecdh_secret)[0:16]`
    (`pairing.c:29-30`, `68-85`, `410-416`), keystream from offset 0.
- Round 2 (`:526-540`): request = 4-byte header with `body[0] == 0` + 64-byte
  client signature (total 68). Response = `200 OK`,
  `Content-Type: application/octet-stream`, **empty** body ->
  `Content-Length: 0`.
  - Verification (`pairing.c:421-456`): the client signature is decrypted with
    the AES-CTR keystream **already advanced by 64 bytes** — the code encrypts
    64 zero bytes first ("One fake round for the initial handshake encryption",
    `pairing.c:441-443`) — then `Ed25519_verify(plaintext,
    server_ecdh_pub || client_ecdh_pub)` with the client key from round 1.
    Replicate this offset exactly.
  - Failure -> `http_response_set_disconnect(1)` (`:534-536`), connection closed
    after the response (`httpd.c:622-625`).

### 4.3 What the shared secret does

`raop_handlers.h:815-861` (inside SETUP, only if `pairing_get_ecdh_secret_key()`
returns non-zero): `aeskey = SHA512(fairplay_decrypted_key_16 || ecdh_secret_32)[0:16]`.
`sha_init()` is **SHA-512** (`crypto.c:505-511` `EVP_sha512()`), truncated to 16
bytes — the inline comment "sha-256" (`raop_handlers.h:857`) is wrong.
`pairing_get_ecdh_secret_key` returns 0 only when no pairing call was ever made
(`STATUS_INITIAL`, `pairing.c:109-119`); after `/pair-setup` alone the secret is
32 zero bytes and is still hashed in. That single `aeskey` feeds both audio
decryption and the mirror-video key derivation (§6).

### 4.4 Can iOS screen mirroring work with pairing stubbed out? — precise answer

**Not cryptographically required, but functionally required unless you also
clear features bit 27.**

Evidence:
1. `/pair-setup` verifies nothing: it only hands out the server Ed25519 public
   key (`raop_handlers.h:435-461`). There is no PIN, SRP proof, registration or
   password check on the path iOS uses for mirroring (`register_client` /
   `check_register` only run in the `use_pin` path, `:504-517`).
2. `/pair-verify` is cryptographically meaningful and its output changes the AES
   key used to decrypt the mirrored video (§4.3). A stub response therefore means
   client and receiver derive different keys: the stream connects but decodes to
   garbage.
3. Whether the client pairs at all is chosen by **features bit 27
   ("SupportsLegacyPairing")**, which UxPlay publishes in the mDNS TXT
   `features` and in the RTSP `/info` `features` (`dnssdint.h:32-34`,
   `dnssd_mdnsd.c:157-161`, `raop_handlers.h:133-135`). **In this checkout the
   active value is `0x5A7FFEE6`, i.e. bit 27 = 1** (`dnssdint.h:32`), so a stock
   build *does* pair with iOS.
4. UxPlay ships the pairing-off variant commented out:
   `//#define FEATURES_1 "0x527FFEE6" /* … bit 27 … OFF */` (`dnssdint.h:33`).
   `0x5A7FFEE6 ^ 0x527FFEE6 = 0x08000000` = bit 27 exactly.
5. `README.md:2184-2197`: *"the client-server 'pairing' step could be avoided
   (leading to a much quicker connection setup, without a 5 second delay) by
   disabling 'Supports Legacy Pairing' (bit 27) … Most clients will then not
   attempt the setup of a 'shared secret key' when pairing … This change is now
   well-tested"*.
6. `raop_handlers.h:830-841`: *"If byte 27 of features ('supports legacy
   pairing') is turned off, the client does not request pairsetup and does NOT
   set up pairing … In this case, ecdh_secret does not exist, so aeskey should
   NOT be hashed with it. UxPlay may be able to function with byte 27 turned off
   because it currently does not support connections with more than one client at
   a time."*

**Two self-consistent options for your C# receiver:**

- **(A) Recommended minimal path:** advertise `features` = **0x527FFEE6** (bit 27
  clear) in both mDNS TXT and `GET /info`; **stub pairing entirely** and do **not**
  hash the FairPlay-decrypted AES key. If the client still sends `/pair-setup`,
  answer `200` with a stable 32-byte key (`application/octet-stream`) or a
  `200/501`; if it sends `/pair-verify` with no preceding pair-setup, UxPlay
  itself answers bare `200 OK` (`raop_handlers.h:470-477`). Caveat: this relies on
  documented client behaviour ("Most clients", `README.md:2188`), which is
  **[not determinable from source]** for a specific iOS version. No SRP/PIN is
  ever needed in this mode.
- **(B) Faithful to this checkout:** advertise `0x5A7FFEE6` (bit 27 set) and
  implement `/pair-setup` + `/pair-verify` exactly per §4.1/§4.2 (Ed25519 key
  pair, X25519 ECDH, AES-CTR-wrapped signature, 64-byte keystream offset in round
  2), then hash the AES key per §4.3. Only Ed25519/X25519/SHA-512/AES-CTR are
  needed (all doable in .NET); no SRP.
- **Never mix them** (bit 27 set + stubbed pair-verify => wrong AES key).

`/pair-setup-pin` (`raop_handlers.h:276-433`) is the only pairing endpoint using
binary plists (`method`/`user` -> `pk`+`salt`; `pk`+`proof` -> `proof`;
`epk`+`authTag` -> `epk`+`authTag`; SRP-6a SHA-1/2048 + AES-GCM,
`pairing.c:500-639`). Reachable only when a pin/password is enabled
(`uxplay.cpp:3152`). Not required for screen mirroring. `/pair-pin-start` is a
PIN announcement with no response body (`raop_handlers.h:251-274`).

## 5. `POST /fp-setup`

`raop_handlers.h:544-589`. Body length decides; Content-Type is not examined.
- `datalen == 16` (`:554`): if `body[4] != 0x03` -> `RTSP/1.0 501 Not
  Implemented` (`:555-559`). Otherwise **142-byte** response (`calloc(142)`,
  `:562`) from `fairplay_setup()`, `Content-Type: application/octet-stream`.
- `datalen == 164` (`:573`): **32-byte** response (`:574`) from
  `fairplay_handshake()`, `Content-Type: application/octet-stream`.
- Any other length: error log, return -> bare `200 OK`, no type/body.
The FairPlay 3 algorithm itself lives in `lib/fairplay_playfair.c` +
`lib/playfair/` (incl. the 483 KB `omg_hax.h` table blob) and is not restated
here; you must port `fairplay_setup` / `fairplay_handshake` / `fairplay_decrypt`.
`POST /fp-setup2` (HTTP/1.1) is explicitly unhandled ->
`HTTP/1.1 421 Misdirected Request` (`http_handlers.h:225-236`).

## 6. SETUP

`raop_handlers.h:599-1067`. Body is a binary plist (`plist_from_bin`, `:626`).
Headers read: `DACP-ID` + `Active-Remote` (`:613-622`), `Authorization` (only if a
password is configured, `:653`), `User-Agent` (only to select the "old protocol"
unhashed-key mode, `:816-825`, `global.h:26`).

Two independent paths in one handler; iOS can send them as two SETUP calls — the
source calls the first "The first SETUP call that initializes keys and timing"
(`:634`) and the second "Process stream setup requests" (`:941`).

Root keys read:

| key | type | notes | line |
|---|---|---|---|
| `ekey` | data (72 B) | `memcpy(eaeskey, ekey, 72)` with **no length check** | 628, 795-800 |
| `eiv` | data (16 B) | AES-CBC IV for audio | 628, 785-787 |
| `deviceID` | string | client MAC | 645-646 |
| `model`, `name` | string | client model/name | 756-759 |
| `isRemoteControlOnly` | bool | true -> "only AirPlay v1 supported" | 864-872 |
| `timingProtocol` | string | `"NTP"` / `"None"` | 873-896 |
| `timingPort` | uint | the **client's** UDP NTP port (destination of the receiver's NTP requests) | 897-907 |

With both `eiv` and `ekey` present ("SETUP 1") the receiver: does digest auth if
configured (`401` + `WWW-Authenticate: Digest realm="raop", nonce="…"`,
`:741-746`); `fairplay_decrypt`s the `ekey` (`:807`) and optionally ECDH-hashes it
(`:826-861`); starts NTP on a local UDP port (`:923-925`); creates the audio
session (`raop_rtp_init`, `:926-927`) and mirror session
(`raop_rtp_mirror_init`, `:928-929`); and returns `timingPort` = its local UDP
port (`:908`, `:934`) and `eventPort` = **0** (`:932-936`; comment `:931`: "the
event port is not used in mirror mode or audio mode").

`streams` array (`:942-1061`):
- **type 110 = mirroring video** (`:955-982`): reads `streamConnectionID` (uint,
  `:959-963`), calls `raop_rtp_mirror_init_aes` + `raop_rtp_mirror_start`
  (opens the TCP listen socket), response stream dict = `{ "dataPort": <uint>,
  "type": 110 }` — only these two keys.
- **type 96 = audio (RAOP)** (`:983-1051`): reads `controlPort` (client's audio
  control port; must be non-zero to enable resend requests, `:991-993`), `ct`
  (`:995-997`), and optionally `spf`, `audioFormat`, `isMedia`, `usingScreen`
  (`:1007-1030`). Response stream dict = `{ "dataPort": <receiver UDP data
  port>, "controlPort": <receiver UDP control port>, "type": 96 }`.
- **any other type** -> error + `http_response_set_disconnect(1)`
  (`:1053-1056`); the 200 response is sent and then the connection is closed.
- Response root = `{ "timingPort": <uint>, "eventPort": 0 }` (only when
  ekey/eiv were present) plus `{ "streams": [...] }` when a `streams` array was
  present (`:1060`); serialized as a **binary plist** with
  `Content-Type: application/x-apple-binary-plist` (`:1063-1066`).
- Keys the client sends that UxPlay **ignores**: per-stream `latency`, `sr`,
  `ss`, `et`; `isScreenMirroring` appears nowhere in the tree. Sample rate is
  hard-coded 44100 (`raop_handlers.h:27`, `:988`).
- The mirror TCP port is dynamic when unconfigured: `dport =
  raop->mirror_data_lport` (`:958`); `netutils_init_socket` binds port 0 and
  writes back the assigned port (`netutils.c:114-197`), which is what the
  response advertises (`:975`).
- If mirror/audio start fails, the 200 response is still sent but with
  disconnect set (`:970-972`, `:1037-1039`).

**Video key derivation to reproduce** (`mirror_buffer.c:41-92`):
`key = SHA512("AirPlayStreamKey" + decimal(streamConnectionID) || aeskey16)[0:16]`,
`iv = SHA512("AirPlayStreamIV" + decimal(streamConnectionID) || aeskey16)[0:16]`,
AES-128-CTR. Then the receiver accepts a TCP connection on `dataPort`
(`raop_rtp_mirror.c:244-246`); each frame = 128-byte header (`[0:4]` big-endian
payload size; `[4:6]` type: `0x00 0x00` encrypted non-IDR, `0x00 0x10` encrypted
IDR, `0x01 0x00` unencrypted SPS+PPS, `0x02 0x00` old-protocol keepalive,
`0x05 0x00` per-second "streaming report") + payload (`raop_rtp_mirror.c:297-363`).

## 7. RECORD / SET_PARAMETER / GET_PARAMETER / TEARDOWN / FLUSH / feedback

- **RECORD** (`raop_handlers.h:1217-1229`): body ignored; response `200 OK` with
  `Audio-Latency: <audio_delay_micros*44100/1e6>` (default `audio_delay_micros` =
  250000 -> `11025`; `raop.c:639`) and
  `Audio-Jack-Status: connected; type=analog`. No body/Content-Type. Nothing is
  started here (sockets are live from SETUP).
- **SET_PARAMETER** (`:1125-1181`): no Content-Type -> `451 Parameter not
  understood`. With `text/parameters`:
  - body starting `volume: ` -> `sscanf("%f")`, AirPlay **dB in [-30, 0]**,
    `-144.0` = mute (`uxplay.cpp:2794-2818`);
  - body starting `progress: ` -> `sscanf("%u/%u/%u", &start,&curr,&end)`
    (`:1156-1159`), audio only;
  - unknown lines ignored silently.
  `image/jpeg` / `image/png` -> coverart (`:1166-1172`);
  `application/x-dmap-tagged` -> metadata (`:1173-1179`). Any other Content-Type
  -> silent empty `200 OK` (**no `Content-Length`**, since no Content-Type is
  echoed). There is **no** `deviceid` and **no** `playback-info` case in
  SET_PARAMETER; `playback-info` is `GET /playback-info` on the HTTP channel
  (`raop.c:468-469`) and `deviceID` is a SETUP key.
- **GET_PARAMETER** (`:1069-1123`): no Content-Type -> `451`. With
  `text/parameters` and a body containing the literal `volume\r\n` -> response
  `Content-Type: text/parameters`, body `volume: %9.6f\r\n` (current dB, e.g.
  `volume:  0.000000\r\n`); other parameter lines logged/ignored. iOS also uses
  it as an RTSP keep-alive.
- **FLUSH** (`:1231-1252`): audio only; optional `RTP-Info: seq=<n>` ->
  `raop_rtp_flush`.
- **TEARDOWN** (`:1254-1325`): body may be a binary plist with
  `streams:[{type:96}|{type:110}]` (`:1265-1282`). Response `200 OK` +
  `Connection: close` (`:1286`); the handler does not itself close the socket. It
  stops `raop_rtp` (96), `raop_rtp_mirror` (110), or destroys both when no stream
  type is listed (`:1288-1318`).
- **POST /feedback** (`:1206-1215`; dispatch `raop.c:408-409`): pure heartbeat;
  empty `200 OK`; UxPlay only resets a counter (`uxplay.cpp:2648-2651`).
  **There is no UDP feedback socket in this implementation** (grep "feedback":
  only these sites). Per-second mirroring feedback arrives inside the mirror TCP
  stream as `0x05 0x00` packets (`raop_rtp_mirror.c:344`). Whether iOS also sends
  UDP feedback that UxPlay ignores is **[not determinable from source]**.

## 8. Ports and sockets

- **Control channel: one TCP port** (`raop->port`, `raop.c:776-779`, `833-838`).
  Without `-p` it is 0 -> OS-assigned, and the real port is used for both the
  RAOP and the AirPlay service (`uxplay.cpp:3155-3164`), advertised via mDNS SRV
  (`dnssd_register_raop(raop_port)`, `dnssd_register_airplay(airplay_port)`,
  `uxplay.cpp:2322-2342`). **7000 only happens with `-p`** (which sets TCP
  7100/7000/7001 and UDP 7011/6001/6000; `uxplay.cpp:1690-1695`, help text
  `:1058`). So the control port is *not* fixed: it must be whatever you publish
  in Bonjour.
- **Mirroring video: TCP**, port returned as `dataPort` for stream type 110
  (configured 7100 with `-p`, else dynamic). Receiver must listen/accept
  (`raop_rtp_mirror.c:863-920`).
- **Audio: two UDP ports** returned as `dataPort`/`controlPort` for stream type
  96 (6000/6001 with `-p`, else dynamic; `raop_rtp.c:241-268`, `691-722`).
- **Timing: one UDP port** returned as `timingPort` (7011 with `-p`, else
  dynamic). The receiver *sends* 32-byte NTP client requests
  (`0x80 0xd2 0x00 0x07 …`) to the client's `timingPort` and reads 128-byte
  responses (`raop_ntp.c:498-536`, `568-654`).
- **`/feedback`: no dedicated socket** — `POST /feedback RTSP/1.0` on the control
  TCP channel.
- mDNS on UDP 5353 (`dnssd_mdnsd.c:351-358`). `_airplay._tcp` TXT:
  `deviceid, features ("0x5A7FFEE6,0x0"), pw, flags ("0x4"), model, pk, pi,
  srcvers, vv` (`dnssd_mdnsd.c:144-169`). `_raop._tcp` TXT:
  `ch, cn, da, et, vv, ft, am, md, rhd, pw, sr, ss, sv, tp, txtvers, sf, vs, vn,
  pk` (`:96-142`; values `dnssdint.h:27-44`). Instance names
  `<NAME>._airplay._tcp.local` and `<MAC-UPPER-NO-COLONS>@<NAME>._raop._tcp.local`
  (`:82-90`).

## 9. Not determinable from this source

1. The exact iOS request ordering (only the two-SETUP pattern is inferable from
   `raop_handlers.h:634` / `:941`).
2. Whether a specific iOS version honours `features` bit 27 = 0 (README claims
   "most clients"; no version matrix).
3. Whether iOS sends extra UDP feedback traffic while mirroring.
4. `/command` and `/stream.xml` (AirPlay 2) — absent here, so no spec available.
5. FairPlay internals: only the I/O sizes (16->142, 164->32, plus
   `fairplay_decrypt` on a 72-byte `ekey`) are documented here.
