# Bundled AdGuard DNS filter

The unmodified [AdGuard DNS filter](https://adguardteam.github.io/HostlistsRegistry/assets/filter_1.txt) is published by [AdGuard](https://github.com/AdguardTeam/AdguardSDNSFilter) and is the same feed referenced by [AdGuard Home](https://github.com/AdguardTeam/AdGuardHome). The upstream header credits AdGuard Base, Social Media, Tracking Protection and Mobile Ads filters, EasyList and EasyPrivacy. It records hostlist-compiler v3.0.0.

- Version: `1.0.87.15`
- Upstream last modified: `2026-11-20T10:11:11.470Z`
- Size: `4,627,121` bytes; SHA-256: `12337ab8a7036634d3723479fd7fade2dd245734dacfd1d66cb4f0589ef1f04c`
- Upstream [GNU GPL version 3 license text](https://github.com/AdguardTeam/AdguardSDNSFilter/blob/master/LICENSE) is supplied locally as `AdGuardSDNSFilter-LICENSE.txt` (upstream Git blob `9cecc1d4669ee8af2ca727a5d8cde10cd8b2d7cc`); this is the licensing notice for the filter data.
- Loader totals: `190,946` indexed block rules and `204` indexed exceptions, with `1` source rule disabled by `$badfilter` and `750` unsupported source rules skipped. Totals count distinct indexed patterns and do not include comments or `$badfilter` directives.

Both platforms embed the same file, this notice, and the license. The feed is parsed once per process on first use; no network or loose filter file is needed. Either the ads or tracking switch enables the entire combined upstream feed; disable both to turn it off. There is no separate bundled malware filter. Custom block and allow lists remain separate.

Only anchored plain hostname rules (`||host^` and `||host^|` for host and descendants; `|host^` and `|host^|` for the exact host) and their `@@` exceptions are indexed. The loader honors `$badfilter` and supports `$important` on indexed blocks (such blocks override upstream ordinary exceptions). Explicit local blocks and allowlists are independent of bundled exceptions. Browser URL/element filters, wildcard and regex patterns, unanchored or ambiguous forms, and unsupported modifiers are retained in the byte-for-byte snapshot but never converted to broader hostname rules. Load-time logs give installed, disabled, and skipped counts; this is not a full AdGuard filtering engine.

When refreshing, replace the raw snapshot without line-ending conversion, verify the new upstream version and SHA-256, and re-run the dual-platform DNS harness. The rule, attribution and license resources are built from this canonical directory; Windows already uses the sibling Android checkout for shared DNS web assets.
