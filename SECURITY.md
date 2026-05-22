# Security Policy

## Reporting a Vulnerability

If you believe you've found a security vulnerability in this plugin,
please report it privately rather than opening a public issue.

**To report:** Use the "Report a vulnerability" button on the
[Security tab](https://github.com/Zigerus/jellyfin-plugin-dtdd/security)
of this repository. See GitHub's
[guide to private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
if you haven't used the workflow before.

Helpful details to include:
- Description of the vulnerability and its potential impact
- Steps to reproduce, or a proof-of-concept
- Affected version (commit SHA or release tag)
- Any suggested mitigations

## Response Expectations

This is a personal homelab project maintained on a best-effort basis.
The goal is to acknowledge reports within 7 days and provide a fix or
mitigation plan within 30 days for confirmed vulnerabilities, with no
formal SLA. Severe issues will be prioritized accordingly.

## Supported Versions

Only the latest release is supported. The plugin is pre-1.0 and may
receive breaking changes between minor versions without backports.
Always test new releases in a non-production Jellyfin instance first.

## Scope

**In scope:**
- The plugin's C# code (`Jellyfin.Plugin.Dtdd/**`)
- The injected web client script (`Web/dtdd-injector.js`)
- The plugin's release artifacts (zip, `manifest.json`)
- The release pipeline (`.github/workflows/**`)

**Out of scope** — please report these upstream:
- Jellyfin server core → [jellyfin/jellyfin](https://github.com/jellyfin/jellyfin/security)
- JavaScript Injector plugin → [n00bcodr/Jellyfin-JavaScript-Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector)
- DoesTheDogDie.com API → contact DTDD directly
- Your Jellyfin server configuration, host OS, or network setup

## Coordinated Disclosure

Once a fix is released, a GitHub Security Advisory will be published
with credit to the reporter (unless anonymity is requested). The
typical disclosure window is 90 days from first report, with flexibility
based on severity and patch availability.
