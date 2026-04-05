# NfsSharp

[![NuGet](https://img.shields.io/nuget/v/NfsSharp.svg?label=NuGet)](https://www.nuget.org/packages/NfsSharp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NfsSharp.svg)](https://www.nuget.org/packages/NfsSharp)
[![CI](https://github.com/itsWindows11/NfsSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/itsWindows11/NfsSharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A full-featured .NET NFS client library supporting NFSv2, NFSv3, and NFSv4 with a single, version-agnostic API.

---

## Features

- **NFSv2, NFSv3, and NFSv4** support with automatic version negotiation
- Single `NfsClient` entry point — no version-specific code in your application
- `NfsStream` integrates seamlessly with all .NET I/O APIs (`StreamReader`, `CopyToAsync`, `JsonSerializer`, etc.)
- **AUTH_NONE**, **AUTH_SYS**, and **RPCSEC_GSS** stub (derive and override to add Kerberos)
- Auto version negotiation: tries NFSv4 → v3 → v2 when `NfsVersion.Auto` is used
- Retry on transient failures (socket errors, timeouts) with exponential back-off
- Fully **seekable** streams — NFS is a random-access protocol
- `SetLength` / `SetLengthAsync` for remote file truncation/extension
- `Flush` / `FlushAsync` issues an NFS `COMMIT` RPC to promote unstable writes to stable storage
- Multi-target: **net9.0**, **net8.0**, and **netstandard2.0**

---

## Installation

```sh
dotnet add package NfsSharp
```

Or search for **NfsSharp** in the NuGet Package Manager UI in Visual Studio.

---

## Quick Start

```csharp
// Connect and mount
await using var nfs = new NfsClient("nfs.example.com", "/exports/data");
await nfs.ConnectAsync();

// List a directory
var entries = await nfs.ReadDirAsync("reports");
foreach (var entry in entries)
    Console.WriteLine($"{entry.Name}  ({entry.Attributes?.Size ?? 0} bytes)");

// Read a file with StreamReader
await using NfsStream stream = await nfs.OpenFileAsync("reports/q4.csv");
using var reader = new StreamReader(stream);
string content = await reader.ReadToEndAsync();
Console.WriteLine(content);

// Write a file
await using var ws = await nfs.CreateFileAsync("output/result.txt");
await using var writer = new StreamWriter(ws);
await writer.WriteLineAsync("Hello from NfsSharp!");
await ws.FlushAsync(); // issues NFS COMMIT
```

---

## Authentication

```csharp
// AUTH_NONE (default — no credentials)
var nfs = new NfsClient("nfs.example.com", "/export");

// AUTH_SYS (Unix credentials)
var creds = new AuthSysCredentials(
    machineName: "myclient",
    uid: 1000,
    gid: 1000,
    gidList: new uint[] { 100, 200 });
var nfs = new NfsClient("nfs.example.com", "/export", creds);

// RPCSEC_GSS (Kerberos stub — derive and override EncodeBody for full support)
// var creds = new MyKerberosCredentials();
```

---

## NfsStream

`NfsStream` is a standard `System.IO.Stream`. It works with every .NET API that accepts a stream:

```csharp
await using NfsStream stream = await nfs.OpenFileAsync("data/archive.json");

// Copy to a local file
using var local = File.Create("archive.json");
await stream.CopyToAsync(local);

// Deserialize JSON directly
var obj = await JsonSerializer.DeserializeAsync<MyData>(stream);

// Truncate / extend a file
await stream.SetLengthAsync(0);   // truncate to empty
await stream.SetLengthAsync(4096); // extend to 4 KiB

// Commit unstable writes to stable storage
await stream.WriteAsync(data);
await stream.FlushAsync(); // issues NFS COMMIT
```

---

## Version Negotiation

```csharp
// Auto: tries NFSv4 → v3 → v2 (default)
var nfs = new NfsClient("server", "/export", NfsVersion.Auto);

// Pin to a specific version
var nfsV3 = new NfsClient("server", "/export", NfsVersion.V3);
var nfsV4 = new NfsClient("server", "/export", NfsVersion.V4);

await nfs.ConnectAsync();
Console.WriteLine($"Negotiated: {nfs.NegotiatedVersion}"); // e.g. V3
```

---

## API Reference

| Method | Description |
|--------|-------------|
| `ConnectAsync()` | Connects, negotiates version, and mounts the export |
| `GetAttrAsync(path)` | Returns attributes (size, type, timestamps) of a path |
| `LookupAsync(path)` | Resolves a path to its file handle and attributes |
| `ReadDirAsync(path)` | Lists directory entries |
| `OpenFileAsync(path)` | Opens a file for reading and writing; returns `NfsStream` |
| `OpenFileReadOnlyAsync(path)` | Opens a file read-only; returns `NfsStream` |
| `CreateFileAsync(path)` | Creates or truncates a file; returns writable `NfsStream` |
| `MkDirAsync(path)` | Creates a directory |
| `RemoveAsync(path)` | Removes a file |
| `RmDirAsync(path)` | Removes an empty directory |
| `RenameAsync(src, dst)` | Renames or moves a file or directory |
| `ReadLinkAsync(path)` | Reads the target of a symbolic link |
| `SymLinkAsync(link, target)` | Creates a symbolic link |
| `LinkAsync(target, link)` | Creates a hard link |
| `FsStatAsync()` | Returns filesystem statistics (total, free, available bytes) |
| `ListExportsAsync()` | Lists all exports advertised by the server |

---

## GitHub Actions Setup

### CI

The CI workflow runs automatically on every push and pull request. No setup is required.

### Publishing to NuGet

1. **Fork / clone** this repository.
2. Go to **Settings → Secrets and variables → Actions**.
3. Click **New repository secret** and add:
   - **Name:** `NUGET_API_KEY`
   - **Value:** Your NuGet.org API key (scoped to **push** on the `NfsSharp` package)
4. Alternatively (recommended), use **NuGet trusted publishing**:
   - On NuGet.org, navigate to your package → **Manage → Trusted publishers → Add GitHub Actions publisher**.
   - Enter your GitHub org/repo and the workflow file name (`publish.yml`).
   - Remove the `--api-key` flag from the push steps in `.github/workflows/publish.yml`.
5. **Tag a release** to trigger the workflow:
   ```sh
   git tag v0.1.0
   git push origin v0.1.0
   ```

---

## Contributing

Contributions are welcome! Please open an issue or pull request on GitHub. For large changes, open an issue first to discuss your approach.

---

## License

MIT — see [LICENSE](LICENSE) for details.
