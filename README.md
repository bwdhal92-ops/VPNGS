# VPNGS - Android Application

VPNGS is a high-performance Android VPN client app built with Kotlin, Jetpack Compose, and Material Design 3.

## Features

- **Protocol Support**: Full support for VLESS, VMess, Trojan, Hysteria2, and TUIC proxy nodes.
- **Multi-Mirror Subscription Fetcher**: Auto-fetches and parses servers from multiple mirrors with offline caching fallback.
- **Smart Probing & Scanning**: Real-time STUN/UDP ping latency measurement, Web reachability check, and download speed estimation.
- **Smart Category Filtering**:
  - **All**: Browse all available proxy servers.
  - **Games (UDP)**: Filters servers that successfully pass UDP probes for low-latency gaming.
  - **Browse**: Filters servers optimized for web browsing.
  - **Stream**: Orders servers by highest estimated download bandwidth for video streaming.
- **Smart Auto-Select**: Automatically routes connections to the highest-performing server based on your active filter.
- **JSON Config Inspector**: Generates production-ready SingBox and Xray JSON configuration files for any server.
- **Custom Links & Subscriptions**: Easily import individual VLESS/VMess links or add custom subscription URLs.
- **User Profile & Settings**: Custom profile display names, TUN mode toggle, SOCKS5 & HTTP proxy port configuration, and theme customization.
- **Diagnostics Console**: Direct manual speed testing and real-time system event logs.

## Tech Stack

- **Language**: Kotlin
- **UI Framework**: Jetpack Compose (Material 3)
- **Architecture**: MVVM with StateFlow
- **Networking**: OkHttp
- **Persistence**: Preferences DataStore & JSON serialization
