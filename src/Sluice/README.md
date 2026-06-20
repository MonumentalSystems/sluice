# Sluice

[![NuGet](https://img.shields.io/nuget/v/Sluice.svg)](https://www.nuget.org/packages/Sluice/)
[![Downloads](https://img.shields.io/nuget/dt/Sluice.svg)](https://www.nuget.org/packages/Sluice/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/MonumentalSystems/sluice/blob/master/LICENSE)

Ark/Lightning payment primitives:

- **barkd REST client** (`IBarkdClient` / `BarkdClient`) — creates invoices, polls settlement, reads balance, lists lightning movements against bark's `barkd` daemon.
- **BOLT11 parsing** — payment-hash extraction and amount decoding from the human-readable prefix.
- **LNURL-pay receive surface** (`GatewayLnurl`) — LUD-06/16 Lightning Address endpoints (receive-only).
- **NIP-47 Nostr Wallet Connect bridge** — exposes the wallet over nostr with NIP-44 v2 crypto (BIP-340 schnorr, ChaCha20, HKDF).

Wallet runtime lives in the barkd pod; this library only talks to it.

## License

MIT
