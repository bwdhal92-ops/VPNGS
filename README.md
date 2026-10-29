# VPNGS Android

Android companion app for VPNGS, implemented with Kotlin and Jetpack Compose. Its visual theme follows the Windows client: dark surfaces, orange controls, a shield-and-flare mark, Arabic labels, server search, manual links, profile, settings, and diagnostics.

## Implemented in this project

- Fetches subscription links from the same FlareFeed mirrors and keeps an offline server-list cache.
- Hides Cloudflare entries by default; the setting can reveal them.
- Measures real TCP connection latency, stores the measurements locally, tests only newly discovered servers on later launches, and supports a manual full TCP rescan.
- Does not fabricate UDP results, download speeds, exit locations, or connection telemetry.
- Saves the display name and settings locally. Anonymous Firebase Auth and Firestore session telemetry use the `VBNGS` collection; refresh tokens are encrypted with Android Keystore. The profile screen describes the collected, limited fields.
- Supports parsing common VLESS, VMess, Trojan, Hysteria2, and TUIC links and generating a configuration preview.

## Not yet integrated

The Android VPN tunnel engine and Android `VpnService` packet bridge are not included in this source tree. Consequently, the app does not claim a connection is active, and UDP, web-through-tunnel, and download measurements are unavailable. The corresponding controls explain this limitation instead of showing simulated results. Add and validate an Android-native sing-box/libbox integration before enabling those features.

Firebase session reporting uses the public web API key configured for the existing Firebase project and the Firestore rules in the parent project. Firebase Anonymous Authentication must be enabled and the parent `firestore.rules` published for the `VBNGS` collection.

## Build environment

Open this folder in Android Studio with the Android SDK and Java 17 installed. This folder currently does not include the Gradle wrapper scripts, so use Android Studio's Gradle integration or add the matching wrapper before command-line builds.
