# Facepunch.Steamworks (UPM repack)

Unofficial UPM repack of the official **Facepunch.Steamworks 2.5.2** release binaries.

- Upstream: https://github.com/Facepunch/Facepunch.Steamworks
- Source of binaries: the `Release/Unity/` folder of the 2.5.2 GitHub release zip
- Support library for the FishNet Steam transport (`FishyFacepunch`) in this project

## Why a repack

Facepunch does not ship a UPM package — upstream distributes a zip of DLLs plus
pre-configured `.meta` files. This package wraps that exact set so it can be
referenced as a normal Unity package.

## Do not remove the .meta files

The three managed assemblies are platform-exclusive and the split lives in the
`.meta` files:

| Assembly | Enabled on |
|---|---|
| `Facepunch.Steamworks.Win32.dll` | Windows x86 standalone + Editor (x86) |
| `Facepunch.Steamworks.Win64.dll` | Windows x64 standalone + Editor (Windows) |
| `Facepunch.Steamworks.Posix.dll` | Linux64 / macOS standalone + Editor (`OS: OSX`) |

If Unity regenerates these metas, all three load at once and the `Steamworks`
namespace is defined twice — the project will not compile.

## Usage

```csharp
if (Steamworks.SteamClient.Init(480))   // 480 = Valve's public test appid
{
    // ...
}

void Update() => Steamworks.SteamClient.RunCallbacks();
void OnDestroy() => Steamworks.SteamClient.Shutdown();
```

`SteamClient.Init` throws on failure — wrap it in try/catch. A sibling
`steam_appid.txt` containing the appid is required when running outside Steam.
