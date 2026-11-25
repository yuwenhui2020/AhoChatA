# DNS core regression harness

Run from the AhoChatA repository with .NET SDK 9:

```powershell
dotnet run --project Dns/Tests/DnsCoreHarness.csproj
```

This dependency-free console harness compiles both complete DNS server sources from the neighboring AhoChatA and MchatW repositories. It supplies only Android Context/Log stubs; it does not claim to build either application or emulate Android networking/TLS trust stores. Tests use loopback fake upstreams, ephemeral listening ports, and a locally generated untrusted TLS certificate. No system DNS changes are requested.

Coverage includes network-order encoding and flags, bounded malformed-name/pointer rejection, preserving response packets with compressed CNAME RDATA pointing into earlier RDATA, concurrent cache transaction IDs, TTL aging, exact/child/boundary domain matching, safe domains not blocked, effective silent-upstream deadlines, UDP mismatch rejection and TCP fallback, fragmented upstream/downstream frame prefixes, persistent downstream TCP, and rejection of an untrusted DoT certificate with a matching hostname.

The harness requires both checkouts to remain siblings. No external test framework or NuGet package is required.
