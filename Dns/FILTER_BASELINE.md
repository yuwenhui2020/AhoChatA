# Bounded DNS filter baseline

The server embeds only these three domain suffix rules:

- Ads: `doubleclick.net`, `googleadservices.com`
- Trackers: `google-analytics.com`
- Malware: no bundled rules (the flag alone provides no malware coverage).

All three exact `||domain^` rules were checked in the maintained AdGuard DNS filter on 2026-11-20:
https://adguardteam.github.io/HostlistsRegistry/assets/filter_1.txt

The filter is a source for a deliberately tiny verified baseline, not a claim that the full AdGuard/EasyList/EasyPrivacy database is bundled. These rules can still affect advertising-dependent or analytics-dependent features. Google documents its Analytics collection endpoint at https://developers.google.com/analytics/devguides/collection/protocol/ga4/reference .

Configure maintained DNS-compatible custom blocklists for broader coverage. The loader accepts domain-only lines, hosts lines (all aliases), and unmodified `||domain^` rules. It rejects URL paths, arbitrary browser rules, exception/modifier syntax, wildcard rules, and malformed names rather than silently treating them as DNS suffixes. Arbitrary browser filter syntax is not supported.

Runtime allow rules override blocks, and suffix matching requires an exact domain or a dot boundary. Public-suffix-wide user rules are not automatically rejected: custom lists and user overrides remain trusted configuration.
