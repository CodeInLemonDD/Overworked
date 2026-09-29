# Overworked

> 🏢 A chaotic PVP party game set in a low-poly office. Think *Overcooked* meets workplace warfare — complete boss tasks, sabotage your coworkers, and survive the 9-to-5 grind.

---

## Overview

2v2 or 1v1. Each round, the Boss assigns a set of tasks. The first team to meet the quota wins. Grab random power-ups to boost your team or disrupt your opponents — coffee spills, flying chairs, printer meltdowns, and more.

---

## Key Features

- **Stylized low-poly 3D** — Vibrant, cartoonish office environments built with Unity URP
- **Physics-driven chaos** — Rigidbody-based interactions: knock over chairs, throw keyboards, slip on banana peels
- **PVP task racing** — 2v2 or 1v1, complete tasks faster than your opponent
- **Random item system** — Traps, buffs, and environmental hazards spawn mid-round
- **Online multiplayer** — Powered by FishNet + Steamworks (Steam P2P/SDR), with NAT punch-through and friend invites

---

## Gameplay

| Element | Description |
|---------|-------------|
| **Mode** | 2v2 or 1v1 |
| **Objective** | Complete boss-assigned tasks and reach the quota before the opposing team |
| **Items** | Offensive traps, defensive buffs, environmental hazards |
| **Win condition** | First team to hit the target quota wins the round |
| **Physics** | Rigidbody-based interactions: thrown objects, knocked-over furniture, slippery floors |

---

## Tech Stack

| Component | Choice |
|-----------|--------|
| Engine | Unity 6000.6.0f1 |
| Render Pipeline | URP (Universal Render Pipeline) |
| Art Style | Low-poly 3D, hand-crafted assets |
| Networking | FishNet (authoritative host logic) |
| Transport Layer | Facepunch.Steamworks (Steam P2P + SDR relay) |
| Platform | Steam (PC) |

---

## Architecture Notes

### Networking Model

- **Host authoritative**: All physics collisions, task progress, and item effects are calculated on the host and synced to clients
- **Topology**: Listen Server (host = one of the players), no dedicated server needed

### Synchronization Strategy

Layered approach to balance visual fidelity with bandwidth efficiency:

| Layer | Objects | Sync Method |
|-------|---------|-------------|
| **Critical** | Players, held items, task objects | High-frequency Transform sync via FishNet (~15 Hz) |
| **Visual** | Knocked-over chairs, thrown projectiles | Host sends initial impulse → clients simulate locally |
| **Local-only** | Particles, screen shake, audio cues | Client-side only, zero network cost |

### Bandwidth Target

| Metric | Value |
|--------|-------|
| Per-client upstream (host) | ~4.5 KB/s |
| Per-client downstream | ~4.5 KB/s |
| Players per match | 2–4 |
| Peak concurrent physics objects | ~20 |

---

## Getting Started

### Prerequisites

- Unity 6000.6.0f1 or later
- URP 17.x (bundled with this Unity version)
- FishNet (install via Package Manager)
- Facepunch.Steamworks (for Steam transport integration)

### Build & Run

```bash
# Clone the repository, then open the folder in Unity
git clone <this repository>
# File → Open Project → Select the cloned directory
```

> Most third-party code here is **vendored into the repository** rather than resolved from a
> registry — FishyFacepunch, Facepunch.Steamworks and the generated prefab collection all
> live under `Assets/`. They have to be committed for a clone to work; a `.gitignore` that
> drops `Assets/` subfolders will silently produce a project that does not compile.
>
> `DEVELOPMENT.md` lists the setup details that are not obvious from the code.

### Package Dependencies

| Package | Version | How it is installed |
|---------|---------|---------------------|
| FishNet | 4.7.3 | UPM git dependency (`Packages/manifest.json`) |
| FishyFacepunch | 4.1.0 | Vendored at `Assets/FishNet/Plugins/FishyFacepunch/` |
| Facepunch.Steamworks | 2.5.2 | **Vendored as a self-made UPM package** at `Packages/com.facepunch.steamworks/` |
| Unity URP | 17.7.0 | Unity Package Manager |
| Unity Input System | 1.20.0 | Unity Package Manager |

> **Facepunch.Steamworks is not distributed as a UPM package.** Upstream ships a zip of DLLs
> plus pre-configured `.meta` files and nothing else, so there is no `?path=` URL to point a
> git dependency at. The package in this repo is a repack of the official 2.5.2 release
> binaries. Its `.meta` files carry the platform split and must not be regenerated — see
> `DEVELOPMENT.md`.
>
> FishNet has no built-in Steam transport. `FishyFacepunch` is the bridge between FishNet and
> Facepunch.Steamworks, and it is a third-party fork. Note that it only works over Steam P2P,
> so it cannot be exercised locally — use the default Tugboat transport for local testing.

---

## Development Roadmap

| Phase | Features | Status |
|-------|----------|--------|
| Prototype | Core movement | **Done** |
| Prototype | Networking (local, Tugboat + Multiplayer Play Mode) | **Done** |
| Prototype | Object interaction — grab, carry, place, throw, grid snapping | **Done** |
| Prototype | Stations — container model, printer, paper box | **Done** |
| Prototype | Task system | **Redesigned** — see below |
| Alpha | 4-player online multiplayer, item system, 3 office maps | Planned |
| Beta | Full task variety, advanced physics interactions, Steam integration | Planned |
| Release | Steam store launch, achievements, community features | Planned |

Local play works today with the default Tugboat transport. Steam transport is wired in but
has never been exercised end to end — it needs a real AppID and two Steam accounts.

**The task system was reshaped partway through.** A task is now an **NPC's request** — a
customer wanting a contract, a colleague wanting a report — rather than carrying a block to a
marked grid cell. The round is meant to be driven by a production chain (computer, printer,
folder) instead. The delivery-to-a-cell loop was dropped, so the rows above describe what was
built rather than what was originally planned.

---

## Contributing

PRs welcome! This is a solo dev project — feedback and bug reports are gold.

---

## License

MIT. The `LICENSE` file itself has not been added to the repository yet.

---

## Acknowledgments

- Inspired by *Overcooked* by Ghost Town Games / Team17
- Built with Unity and the amazing indie dev community
- Special thanks to everyone who tests the chaos

---

> 🐛 Found a bug? That's just office life. Report it anyway.
