# AirPlay receiver mDNS/Bonjour advertisement — implementation-ready spec

Extracted verbatim from UxPlay 1.74 source at
`C:\Users\Administrator\AppData\Local\Temp\airplay-ref\uxplay\UxPlay-master`.

Default DNS-SD backend on all platforms except macOS is the built-in one
(`lib/mdnsd/`, `cmake -DUSE_DNS_SD=1` selects the external Bonjour/Avahi backend —
`CMakeLists.txt:57-73`). Line numbers below are for the checkout above.

Call graph (order matters, TXT is built at registration time):

| step | file:line | effect |
|---|---|---|
| build name | `uxplay.cpp:97-98`, `3563-3565`, `1416-1438` | `server_name` = `"UxPlay"` + `"@"` + hostname unless `-nh` |
| pick MAC | `uxplay.cpp:3599-3615` | `mac_address`, `parse_hw_addr` → 6-byte `hw_addr` |
| `start_dnssd` | `uxplay.cpp:2366-2486` | `dnssd_init(name, len, hw_addr, 6, pin_pw)`, then per-bit `dnssd_set_airplay_features()` |
| set pk | `uxplay.cpp:3644`→`3166-3171`→`raop.c:816-820` | `dnssd_set_pk(raop->pk_str)` |
| register | `uxplay.cpp:2317-2347`, called at `3667` | `dnssd_register_raop(dnssd, raop_port)` then `dnssd_register_airplay(dnssd, airplay_port)` |

---

## 1. Service types and instance naming

Exactly **two** service types are advertised plus one meta-DNS-SD PTR:

| service type | instance (owner name) | built at |
|---|---|---|
| `_airplay._tcp.local` | `"<server_name>._airplay._tcp.local"` | `lib/mdnsd/dnssd_mdnsd.c:87-90`, `174-179` |
| `_raop._tcp.local` | `"<HWADDR>@<server_name>._raop._tcp.local"` | `lib/mdnsd/dnssd_mdnsd.c:82-85`, `181-189` |
| `_services._dns-sd._udp.local` | PTR → both of the above types (not a service) | `lib/mdnsd/mdnsd.c:617` |

Verbatim format strings:

```c
/* dnssd_mdnsd.c:82 */
snprintf(dnssd->raop_name, ..., "%s@%s._raop._tcp.local", hwaddr, dnssd_public->name);
/* dnssd_mdnsd.c:87 */
snprintf(dnssd->airplay_name, ..., "%s._airplay._tcp.local", dnssd_public->name);
/* dnssd_mdnsd.c:174 / 184 */
"_airplay._tcp.local" / "_raop._tcp.local"
```

- `<server_name>` default = `DEFAULT_NAME "UxPlay"` (`uxplay.cpp:82,97-98`) with `@<hostname>`
  appended (`append_hostname`, `uxplay.cpp:1416-1438`; call site `3563-3565`, disabled by `-nh`).
  So the real default is e.g. `UxPlay@DESKTOP-ABC`. `-n <name>` replaces the whole string.
- `<HWADDR>` = the 6-byte MAC as **12 UPPERCASE hex digits, no separators**
  (`utils.c:93-115`: `'A' + hi-10`).
- The names are NOT sanitised; `dnssd_sanitize_label()` (`dnssd_mdnsd.c:48-60`) is applied
  only to the host name. `dnssd.c:54-64` rejects a name equal to `".local"` and truncates
  the name at the first `".local"` substring.
- The external-Bonjour backend produces the same names: `servname = "<HWADDR>@<name>"`
  registered as `_raop._tcp` (`dns_sd.c:295-317`) and `<name>` registered as `_airplay._tcp`
  (`dns_sd.c:378-385`).

**No other records exist in this source.** There is no `_device-info._tcp`, no
`_touch-able._tcp`, no `_appletv-v2._tcp`, no `_rdlink._tcp`, no `_raop._udp`, no
`_airplay._udp` anywhere in the tree (grep over the whole checkout).

## 2. Port advertised by each service

Both services advertise the **same** port: the AirPlay RTSP/HTTP TCP port.

- `_airplay._tcp` SRV port = `airplay_port` (`dnssd_mdnsd.c:176`), and
  `airplay_port = raop_port` (`uxplay.cpp:3163-3164`).
- `_raop._tcp` SRV port = `raop_port` (`dnssd_mdnsd.c:186`), same value.
- `raop_port` is whatever `httpd_start()` actually bound: `netutils_init_socket()` calls
  `bind()` then `getsockname()` and writes the real port back
  (`lib/netutils.c:114-189`; `lib/httpd.c:670-694`).
- **Port 7000 is NOT a compile-time constant.** Default `tcp[3] = {0,0,0}`
  (`uxplay.cpp:150`) → `raop->port = tcp[1] = 0` (`raop.c:789-794`) → a random ephemeral
  port. Only the bare `-p` option sets `tcp[0]=7100; tcp[1]=7000; tcp[2]=7001` and
  `udp[0]=7011; udp[1]=6001; udp[2]=6000` (`uxplay.cpp:1690-1693`); `-p n` / `-p n1,n2,n3`
  override it (`README.md:1415-1424`). `tcp[0]` (mirror-data port) and `tcp[2]` are not
  advertised. So: **7000 = the value of `tcp[1]` when `-p` is used; otherwise dynamic.**

SRV wire format used (`mdnsd.c:168-182`): priority 0, weight 0, port (network order),
target = host name (`<host>.local`).

## 3. Complete TXT records

TXT encoding: each entry = 1 length byte followed by the ASCII text `"key=value"`
(`mdnsd.c:981-995`). Item length ≤ 255; whole record buffer 512 (`mdnsd.h:21`).
The Bonjour backend sets exactly the same key/value set (different insertion order,
`dns_sd.c:256-292` and `344-375`).

### 3.1 `_airplay._tcp` TXT — full set, in emitted order (`dnssd_mdnsd.c:160-168`)

| key | value | source |
|---|---|---|
| `deviceid` | MAC as **lowercase hex with colons**: `aa:bb:cc:dd:ee:ff` (17 chars) | `utils.c:117-142` |
| `features` | `"0x%X,0x%X"` of `features1,features2` → **`0x527FFEE6,0x0`** by default | `dnssd_mdnsd.c:157`, `dnssdint.h:32-34`, `uxplay.cpp:2484` |
| `pw` | `"false"` when `pin_pw==0`, else `"true"` | `dnssd_mdnsd.c:162`, `uxplay.cpp:166` |
| `flags` | **`"0x4"` literal, always** (not the `AIRPLAY_FLAGS "0x84"` macro, which is dead code) | `dnssd_mdnsd.c:163`; macro unused — only definition is `dnssdint.h:47` |
| `model` | `"AppleTV3,2"` (`GLOBAL_MODEL`) | `global.h:21` |
| `pk` | 64 lowercase hex chars = raw 32-byte Ed25519 public key | `raop.c:664-674`, `crypto.h:82` |
| `pi` | **`"2e388006-13ba-4041-9a67-25dd4a43d536"`** — a *constant* in UxPlay, not per-device | `dnssdint.h:49` |
| `srcvers` | `"220.68"` (`GLOBAL_VERSION`) | `global.h:22`, `dnssdint.h:46` |
| `vv` | `"2"` | `dnssdint.h:48` |

`flags` value semantics are not documented in this source; `dns_sd.c:352` only says
`// flags is a string representing a 20-bit flag (up to 3 hex digits)`.

### 3.2 `_raop._tcp` TXT — full set, in emitted order (`dnssd_mdnsd.c:123-141`)

| key | literal value | macro |
|---|---|---|
| `ch` | `2` | `RAOP_CH "2"` `dnssdint.h:28` |
| `cn` | `0,1,2,3` | `RAOP_CN "0,1,2,3"` `:29` |
| `da` | `true` | `RAOP_DA "true"` `:38` |
| `et` | `0,3,5` | `RAOP_ET "0,3,5"` `:30` |
| `vv` | `2` | `RAOP_VV "2"` `:31` |
| `ft` | `0x527FFEE6,0x0` (same `"0x%X,0x%X"` features string) | `dnssd_mdnsd.c:120,128` |
| `am` | `AppleTV3,2` | `GLOBAL_MODEL` `global.h:21` |
| `md` | `0,1,2` | `RAOP_MD "0,1,2"` `:43` |
| `rhd` | `5.6.0.0` | `RAOP_RHD "5.6.0.0"` `:35` |
| `pw` | `false` / `true` | `dnssd_mdnsd.c:103-118` |
| `sr` | `44100` | `RAOP_SR "44100"` `:39` |
| `ss` | `16` | `RAOP_SS "16"` `:40` |
| `sv` | `false` | `RAOP_SV "false"` `:37` |
| `tp` | `UDP` | `RAOP_TP "UDP"` `:42` |
| `txtvers` | `1` | `RAOP_TXTVERS "1"` `:27` |
| `sf` | `0x4` default; `0x8c` if `pin_pw==1`; `0x84` if `pin_pw` 2 or 3 | `dnssd_mdnsd.c:104-118` |
| `vs` | `220.68` | `RAOP_VS GLOBAL_VERSION` `:41` |
| `vn` | `65537` | `RAOP_VN "65537"` `:44` |
| `pk` | same 64-hex Ed25519 key as `_airplay` | `dnssd_mdnsd.c:141`, `raop.c:818` |

Pin/password switch verbatim (`dnssd_mdnsd.c:106-118`):

```c
switch (dnssd_public->pin_pw) {
case 1:      pw = "true"; sf = "0x8c"; break;   /* onscreen pin   */
case 2: case 3: pw = "true"; sf = "0x84"; break;/* password       */
default:     break;                             /* pw="false", sf=RAOP_SF="0x4" */
}
```
`dns_sd.c:270,276` comments: `sf` bit 3 `0x08` = pin required, bit 7 `0x80` = password required.

### 3.3 Keys that DO NOT exist in this source

`gid`, `serialNumber`, `osvers`, `manufacturer`, `protovers`, `acl`, `btlk` are **absent**
from both TXT records (and from the whole tree, except `protovers_node` in the `/info`
plist response — `lib/http_handlers.h:83-84`, which is an HTTP plist, not DNS-SD).
Do not invent them from this reference; they are not determinable here.

### 3.4 `pk` format

`pk` = `utils_hex_to_string(public_key, 32)` → `snprintf(pos, 3, "%2.2x", byte)` per byte
(`utils.c:181-189`): **lowercase hex, 64 chars, no separators**. The key is a raw Ed25519
public key (`ED25519_KEY_SIZE 32`, `crypto.h:82`) generated/persisted by
`ed25519_key_generate()` via `pairing_init_generate()` (`raop.c:653-674`, `pairing.c:87-99`).
A fixed key can be forced by uncommenting `#define PK "b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7"`
(`dnssdint.h:25`, used at `raop.c:666-667`).

## 4. `features` assembly

Stored as two `uint32_t` (`dnssd.h:45-46`), parsed from the strings with `strtoul(...,16)`
(`dnssd.c:75-89`), emitted as `"0x%X,0x%X"` **high-32-second**:
`features = ((uint64)features2 << 32) + features1` (`dnssd.c:155-159`).
Bit `n<32` → `features1` bit `n`; bit `n>=32` → `features2` bit `n-32`
(`dnssd_set_airplay_features`, `dnssd.c:165-182`).

Header defaults (`dnssdint.h:32-34`):
```c
#define FEATURES_1 "0x5A7FFEE6" /* first 32 bits of features, with bit 27 ("supports legacy pairing") ON */
//#define FEATURES_1 "0x527FFEE6" /* first 32 bits of features, with bit 27 ("supports legacy pairing") OFF */
#define FEATURES_2  "0x0"        /* second 32 bits of features */
```

These defaults are then **overwritten bit-by-bit at runtime** in `start_dnssd()`
(`uxplay.cpp:2389-2484`). Bits 0-31 exactly reproduce `0x5A7FFEE6`, then:

| line | bit | value | comment in source |
|---|---|---|---|
| 2393 | 0 | `hls_support` (default 0) | `AirPlay video supported` |
| 2394 | 1 | 1 | `photo supported` |
| 2395 | 2 | 1 | `video protected with FairPlay DRM` |
| 2396 | 3 | 0 | `volume control supported for videos` |
| 2398 | 4 | 0 | `http live streaming (HLS) supported` |
| 2399 | 5 | 1 | `slideshow supported` |
| 2400 | 6 | 1 | (blank) |
| **2401** | **7** | **1** | **`mirroring supported`** |
| 2403 | 8 | 0 | `screen rotation supported` |
| 2404 | 9 | 1 | `audio supported` |
| 2405 | 10 | 1 | |
| 2406 | 11 | 1 | `audio packet redundancy supported` |
| 2408 | 12 | 1 | `FaiPlay secure auth supported` |
| 2409 | 13 | 1 | `photo preloading supported` |
| 2410 | 14 | 1 | `Authentication bit 4: FairPlay authentication` |
| 2411 | 15 | 1 | `Metadata bit 1: Artwork` |
| 2413 | 16 | 1 | `Metadata bit 2: Soundtrack Progress` |
| 2414 | 17 | 1 | `Metadata bit 0: Text (DAACP) "Now Playing"` |
| 2415 | 18 | 1 | `Audio format 1` |
| 2416 | 19 | 1 | `Audio format 2: must be set for AirPlay 2 multiroom audio` |
| 2418 | 20 | 1 | `Audio format 3 ...` |
| 2419 | 21 | 1 | `Audio format 4` |
| 2420 | 22 | 1 | `Authentication type 4: FairPlay authentication` |
| 2421 | 23 | 0 | `Authentication type 1: RSA Authentication` |
| 2423 | 24 | 0 | |
| 2424 | 25 | 1 | |
| 2425 | 26 | 0 | `Has Unified Advertiser info` |
| 2426→2484 | 27 | `setup_legacy_pairing` (**default 0**) | `Supports Legacy Pairing` |
| 2428 | 28 | 1 | |
| 2429 | 29 | 0 | |
| 2430 | 30 | 1 | `RAOP support: with this bit set, the AirTunes service is not required.` |
| 2431 | 31 | 0 | |
| 2475/2476 | 0 and 4 | `hls_support` | `needed for HLS video support` |
| 2481 | 42 | `h265_support` (default 0) | `needed for h265 video support` |
| 2484 | 27 | `setup_legacy_pairing` | `bit 27 ... determines whether the AirPlay2 client-pairing protocol will be used (1) or not (0)` |

`hls_support` ← `-hls` (`uxplay.cpp:2117-2118`); `h265_support` ← `-h265` (`2139-2140`);
`setup_legacy_pairing` ← `-pin` (`1967-1969`) or `-p2p` (`1987`), and forced back to false by
`-pw` (`2029`).

**Resulting advertised values (computed, not literal in the source):**

| config | features1 | features2 | TXT `features=` / `ft=` |
|---|---|---|---|
| default (no options) | `0x527FFEE6` | `0x0` | `0x527FFEE6,0x0` |
| `-pin` (pin_pw=1) | `0x5A7FFEE6` | `0x0` | `0x5A7FFEE6,0x0` |
| `-pw` (pin_pw=2/3) | `0x527FFEE6` | `0x0` | `0x527FFEE6,0x0` |
| default + `-hls` | `0x527FFEF7` | `0x0` | `0x527FFEF7,0x0` |
| default + `-h265` | `0x527FFEE6` | `0x400` | `0x527FFEE6,0x400` |
| default + `-hls -h265` | `0x527FFEF7` | `0x400` | `0x527FFEF7,0x400` |

**Which bits are required for Screen Mirroring:** the source identifies only bit 7 as
`mirroring supported` (`uxplay.cpp:2401`) and bit 30 as making `_raop._tcp` unnecessary
(`2430`). It never states a minimal mirroring-only set, and it always advertises the full
`0x5A7FFEE6`-derived set. Anything beyond "bit 7 must be set, and keep bit 2 / 12 / 14 / 22
(FairPlay) as UxPlay does" is **inference, not source fact**. The README only adds that
bit 27 controls whether clients run the AirPlay 2 pairing handshake and that UxPlay works
whether it claims AppleTV3,2 / 220.68 or AppleTV6,2 / 380.20.1 (`README.md:2184-2214`).

## 5. Host name, `_device-info._tcp`, Bluetooth LE

Host name (`dnssd_mdnsd.c:62-72` + `48-60`):
```
host = gethostname(); if empty -> "UxPlay";
truncate at first '.'; replace every char not [A-Za-z0-9-] with '-';
host_name = "<host>.local"
```
On Windows that is the computer name (e.g. `DESKTOP-ABC.local`). It is the SRV target and
the owner of the A/AAAA records.

**No `_device-info._tcp` record is registered** — grep over the entire checkout finds no
occurrence of `device-info`. The only other DNS-SD record emitted is the
`_services._dns-sd._udp.local` PTR → service type (`mdnsd.c:617`).

Bluetooth LE beacon (only when UxPlay is run with `-ble <file>` and `uxplay-beacon.py`):
BLE file written by `write_bledata()` (`uxplay.cpp:419-433`) = 2-byte little-endian
`raop_port`, then 4-byte little-endian pid, then the process name string.
The advertisement is Apple manufacturer-specific data, company ID `0x004C`, payload
(`uxplay_beacon_module_BlueZ.py:114-120`, `uxplay_beacon_module_winrt.py:61-71`,
`uxplay_beacon_module_BleuIO.py:68-74`, `uxplay_beacon_module_HCI.py:111-115`):

```
0x09 0x08 0x13 0x30 <ipv4:4 bytes> <port:2 bytes big-endian>
```
comment: `Apple Data Unit type 9 (Airplay), length 8, flags 0001 0011, seed 30`
(BleuIO prepends `0xff 0x4c 0x00` and a total length byte `0x0d` — i.e. 14-byte AD
structure; `BLEUIO...py:68-75`). `flags 0x13`, `seed 0x30`. Non-connectable advertising,
default interval 100 ms on BLE channels 37/38/39 (`README.md:1801-1808`).

## 6. mDNS protocol details (internal `mdnsd` backend)

- Multicast IPv4 `224.0.0.251:5353`, IPv6 `ff02::fb:5353` (`mdnsd.c:28-30`).
- Socket: bind `0.0.0.0:5353` (+`[::]:5353`), `SO_REUSEADDR`/`SO_REUSEPORT`,
  `IP_MULTICAST_TTL = 255`, `IP_MULTICAST_LOOP = 1`, `IP_MULTICAST_IF` = chosen interface
  address, `IP_ADD_MEMBERSHIP 224.0.0.251` (`mdnsd.c:479-535`); IPv6 `IPV6_V6ONLY=1`,
  `IPV6_MULTICAST_HOPS=255`, `IPV6_MULTICAST_LOOP=1`, join `ff02::fb` (`mdnsd.c:537-586`).
  Source port of responses is therefore 5353.
- **On Windows IPv6 is disabled**: `mdns_get_default_ipv6()` returns 0 under `WIN32`
  (`mdnsd.c:421-426`) so `sock_fd6 == -1` → IPv4-only mDNS. IPv4 address selection
  (`mdns.c:348-391`, `_WIN32` branch) uses `GetAdaptersAddresses` and requires
  `OperStatus == IfOperStatusUp`, a non-null `FirstGatewayAddress`, and a description not
  containing `Hyper-V`, `Virtual`, `WSL`, `vEthernet`, skipping loopback.
- Queries answered, `mdns_question_matches_service()` (`mdnsd.c:812-857`), name compare is
  case-insensitive (`331-341`):
  - `_services._dns-sd._udp.local` PTR → both service types;
  - `<type>.local` PTR → instance name;
  - `<instance>` TXT or SRV → that service (and the host A/AAAA are added);
  - `<host>.local` A or AAAA → host address only.
  `DNS_TYPE_ANY` (255) matches every type (`343-346`).
- Response packet if any answer matched (`mdns.c:588-655`): header QDCOUNT=0,
  flags = `DNS_FLAG_RESPONSE|DNS_FLAG_AUTHORITATIVE` = `0x8000|0x0400` = **0x8400**
  (QR=1, AA=1, RD=0), then ANCOUNT = 4 per service (+1 for host), NSCOUNT=0, ARCOUNT=0.
  All records go in the **answer** section; nothing in the additional section.
- Cache-flush bit `0x8000` in the class field is set on **SRV, TXT, A, AAAA** but **not on
  PTR** (`mdnsd.c:160,172,188,204,227`).
- **TTLs** (`mdnsd.h:22`, `mdnsd.c:33`): `MDNSD_TTL_SERVICE 4500` s for PTR **and TXT**;
  `MDNS_TTL_HOST 120` s for **SRV**, A and AAAA (`mdnsd.c:619-621, 647-648`). Goodbye
  packets use TTL 0 (`mdnsd_goodbye`, `mdnsd.c:1143-1153`).
- **Unicast**: the receiver ignores packets with the QR bit set (`mdnsd.c:876-878`), then
  for each question records `unicast_reply` if the class has the QU bit `0x8000`
  (`896-898`), and after the multicast response also sends a copy directly to the querier
  if QU was set **or** the source port != 5353 (`907-911`). Note it multicast-answers
  legacy (port != 5353) queries too, which RFC 6762 §6.7 does not require.
- **Probing/announcing**: there is **no** probe, no tie-break, no conflict detection and no
  name-conflict handling. `mdnsd_start()` just opens sockets and starts the thread
  (`mdnsd.c:1023-1076`); `dnssd_register_raop`/`_airplay` call `mdnsd_announce(mdnsd, MDNSD_TTL_SERVICE)`
  once immediately after the (idempotent) start (`dnssd_mdnsd.c:256-259`, `281-284`), which
  sends one combined IPv4 and one combined IPv6 packet (`mdns.c:806-810`) — i.e. one
  gratuitous announcement per registration, not the RFC 6762 §8.3 repeated 3-announcement
  sequence.
- Service record set per service (`mdns.c:609-622`), 4 records:
  `_services._dns-sd._udp.local PTR <type>` , `<type> PTR <instance>` ,
  `<instance> SRV 0 0 <port> <host>.local` , `<instance> TXT <txt>`.
- Combined-packet cap: `MDNS_COMBINED_PACKET_MAX 1200`; if airplay+raop do not fit,
  separate packets are sent (`mdns.c:32, 698-700, 768-804`). Max packet 1500.
- Query loop: 250 ms `select()` timeout, query parsing ignores anything after a malformed
  name (`mdns.c:916-979`, `883-901`).

## 7. Minimum set sufficient for mirroring

Determinable from the source:

- Both services are always registered, and failure of either is fatal unless BLE discovery
  is configured (`uxplay.cpp:2322-2342`). There is **no code path** that registers only
  `_airplay._tcp`.
- Feature bit 30 (`0x40000000`, ON by default) is commented
  `"RAOP support: with this bit set, the AirTunes service is not required."`
  (`uxplay.cpp:2430`) — so per the source's own comment, `_raop._tcp` is not strictly
  required *for a receiver that sets bit 30*. Whether iOS 15-18 in practice lists a
  mirroring-only device with `_airplay._tcp` alone is **not determinable from this source**;
  UxPlay never tests that configuration.
- The smallest record set the implementation actually emits and answers for one service is
  the 4-record set in §6 plus the host A record; a mirroring-only receiver that skips
  `_raop._tcp` would still need `_airplay._tcp` PTR/SRV/TXT + `<host>.local` A, and
  `pk`/`pi`/`deviceid`/`features`(bit 7 + FairPlay bits)/`flags`/`model`/`srcvers`/`vv`/`pw`.
- `-pin`/`-pw` (client access control) are optional: default `pin_pw = 0`
  (`uxplay.cpp:166`) gives `pw=false`, `sf=0x4`, `flags=0x4`, and no pairing requirement.

## 8. Unknowns (explicitly not determinable from this source)

1. Meaning of the individual bits of the `flags` TXT value (only the literal `0x4` is used;
   `AIRPLAY_FLAGS "0x84"` is defined but never referenced).
2. Whether iOS 15-18 lists a mirroring device that advertises only `_airplay._tcp` without
   `_raop._tcp`.
3. Any TXT keys of the real AirPlay 2 specification that UxPlay does not emit
   (`gid`, `serialNumber`, `osvers`, `manufacturer`, `protovers`, `acl`, `btlk`, `ft`'s
   exact bit semantics beyond the feature table above).
4. Real device-list behaviour/quirks of iOS: not testable from source.
