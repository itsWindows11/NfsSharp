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
- Multi-target: **net10.0**, **net9.0**, **net8.0**, and **netstandard2.0**

---

## Installation

```sh
dotnet add package NfsSharp
```

Or search for **NfsSharp** in the NuGet Package Manager UI in Visual Studio.

---

## Quick Start

```csharp
using System.IO;

// Connect and mount
await using var nfs = new NfsClient("nfs.example.com", "/exports/data");
await nfs.ConnectAsync();

// List a directory
var entries = await nfs.ReadDirAsync("reports");
foreach (var entry in entries)
    Console.WriteLine($"{entry.Name}  ({entry.Attributes?.Size ?? 0} bytes)");

// Read a file with StreamReader
await using NfsStream stream = await nfs.OpenFileAsync("reports/q4.csv", FileAccess.Read);
using var reader = new StreamReader(stream);
string content = await reader.ReadToEndAsync();
Console.WriteLine(content);

// Write-only stream to a newly created/truncated file
await using var ws = await nfs.OpenFileAsync("output/result.txt", FileAccess.Write, create: true);
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
await using NfsStream stream = await nfs.OpenFileAsync("data/archive.json", FileAccess.ReadWrite);

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

## API Reference (`NfsClient`)

### Properties

| Property | Description |
|--------|-------------|
| `NegotiatedVersion` | NFS protocol version negotiated after `ConnectAsync()` |
| `RootHandle` | Root file handle of the mounted export (available after connect) |

### Methods

| Method | Description |
|--------|-------------|
| `ConnectAsync(ct = default)` | Connects, negotiates version, and mounts the export |
| `GetAttrAsync(path, ct = default)` | Gets attributes for a path |
| `GetAttrAsync(handle, ct = default)` | Gets attributes for an existing file handle |
| `SetAttrAsync(path, attrs, ct = default)` | Applies attributes to a path |
| `SetAttrAsync(handle, attrs, ct = default)` | Applies attributes to an existing file handle |
| `ExistsAsync(path, ct = default)` | Returns whether a path exists |
| `ExistsAsync(handle, ct = default)` | Returns whether a handle still resolves on the server |
| `LookupAsync(path, ct = default)` | Resolves a path to `(Handle, Attributes)` |
| `ReadDirAsync(path, ct = default)` | Lists entries in a directory path |
| `ReadDirAsync(handle, ct = default)` | Lists entries in a directory handle |
| `ReadDirStreamAsync(path, ct = default)` | Streams directory entries by path (paged, lazy) |
| `ReadDirStreamAsync(handle, ct = default)` | Streams directory entries by handle (paged, lazy) |
| `ReadDirRecursiveAsync(path, ct = default)` | Recursively streams descendant entries by path |
| `ReadDirRecursiveAsync(handle, baseRelativePath = "", ct = default)` | Recursively streams descendant entries by handle |
| `ReadLinkAsync(path, ct = default)` | Reads the target of a symbolic link |
| `RemoveAsync(path, ct = default)` | Removes a file |
| `RmDirAsync(path, ct = default)` | Removes an empty directory |
| `MkDirAsync(path, attrs = null, ct = default)` | Creates a directory |
| `RenameAsync(sourcePath, destPath, ct = default)` | Renames or moves a file/directory |
| `LinkAsync(targetPath, linkPath, ct = default)` | Creates a hard link |
| `SymLinkAsync(linkPath, linkTarget, ct = default)` | Creates a symbolic link |
| `FsStatAsync(ct = default)` | Returns filesystem statistics for the mounted export |
| `ListExportsAsync(ct = default)` | Lists exports advertised by the server |
| `OpenFileAsync(path, access = FileAccess.ReadWrite, create = false, ct = default)` | Opens an `NfsStream` with read/write/read-write access; optional create/truncate |
| `DownloadFileToLocalAsync(remotePath, localPath, degreeOfParallelism = 4, chunkSize = 4 * 1024 * 1024, progress = null, ct = default)` | Fast-path download to local disk using parallel ranged reads |
| `UploadFileFromLocalAsync(localPath, remotePath, degreeOfParallelism = 4, chunkSize = 4 * 1024 * 1024, progress = null, ct = default)` | Fast-path upload from local disk using parallel ranged writes |
| `Dispose()` | Disposes client resources |
| `DisposeAsync()` | Asynchronously disposes client resources |

### Constructors

| Constructor | Description |
|--------|-------------|
| `NfsClient(server, version = NfsVersion.Auto, nfsPort = 0, mountPort = 0, connectTimeout = null, readTimeout = null)` | Creates a client with `AUTH_NONE` credentials and automatic export discovery at connect time |
| `NfsClient(server, credentials, version = NfsVersion.Auto, nfsPort = 0, mountPort = 0, connectTimeout = null, readTimeout = null)` | Creates a client with explicit credentials and automatic export discovery at connect time |
| `NfsClient(server, exportPath, version = NfsVersion.Auto, nfsPort = 0, mountPort = 0, connectTimeout = null, readTimeout = null)` | Creates a client with `AUTH_NONE` credentials and explicit export path |
| `NfsClient(server, exportPath, credentials, version = NfsVersion.Auto, nfsPort = 0, mountPort = 0, connectTimeout = null, readTimeout = null)` | Creates a client with explicit credentials and explicit export path |

> Note: automatic export discovery requires the server to advertise exactly one export. If multiple exports exist, pass `exportPath` explicitly.

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
4. For **trusted publishing**:
   - On NuGet.org, navigate to your package → **Manage → Trusted publishers → Add GitHub Actions publisher**.
   - Enter your GitHub org/repo and workflow file name (`publish.yml`).
   - Keep `NuGet/login@v1` in `.github/workflows/publish.yml`; it exchanges OIDC for a short-lived `NUGET_API_KEY` output used by `dotnet nuget push`.
5. **Tag a release** to trigger the workflow:
   ```sh
   git tag v0.1.0
   git push origin v0.1.0
   ```

---

## We accept donations

If you would like to support ongoing development:

- GitHub Sponsors: https://github.com/sponsors/itsWindows11
- GitHub profile: https://github.com/itsWindows11

---

## Contributing

Contributions are welcome! Please open an issue or pull request on GitHub. For large changes, open an issue first to discuss your approach.

---

## License

MIT — see [LICENSE](LICENSE) for details.
