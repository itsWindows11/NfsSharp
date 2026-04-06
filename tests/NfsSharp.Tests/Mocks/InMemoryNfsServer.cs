using NfsSharp.Protocol;

namespace NfsSharp.Tests.Mocks;

/// <summary>
/// In-memory NFS-like file-system model used by test doubles.
/// </summary>
internal sealed class InMemoryNfsServer
{
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _directories = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes the in-memory server with a root directory.
    /// </summary>
    public InMemoryNfsServer()
    {
        RootHandle = NewHandle();
        string rootKey = Key(RootHandle);
        _nodes[rootKey] = new Node
        {
            Type = NfsFileType.Directory,
            Size = 0,
            Mode = 0b111_101_101,
            AccessTime = DateTimeOffset.UtcNow,
            ModifyTime = DateTimeOffset.UtcNow,
            ChangeTime = DateTimeOffset.UtcNow,
        };
        _directories[rootKey] = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Gets the root directory handle.</summary>
    public NfsFileHandle RootHandle { get; }

    /// <summary>Creates a regular file in the root directory.</summary>
    public NfsFileHandle CreateRootFile(string name, byte[]? initialData = null, uint mode = 0b110_100_100)
        => CreateFile(RootHandle, name, new NfsSetAttributes { Mode = mode }, initialData ?? Array.Empty<byte>());

    /// <summary>Reads a range of bytes from a file.</summary>
    public NfsReadResult Read(NfsFileHandle handle, long offset, int count)
    {
        var node = GetFileNode(handle);
        int available = (int)Math.Min(count, Math.Max(0, node.Data.Count - offset));
        byte[] buffer = new byte[available];
        if (available > 0)
            node.Data.CopyTo((int)offset, buffer, 0, available);
        bool eof = offset + available >= node.Data.Count;
        TouchAccess(node);
        return new NfsReadResult(buffer, eof);
    }

    /// <summary>Writes bytes to a file and returns the number of bytes written.</summary>
    public int Write(NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count)
    {
        var node = GetFileNode(handle);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

        int targetLength = checked((int)offset + count);
        if (targetLength > node.Data.Count)
        {
            if (targetLength > node.Data.Capacity)
                node.Data.Capacity = targetLength;
            while (node.Data.Count < targetLength)
                node.Data.Add(0);
        }

        for (int i = 0; i < count; i++)
            node.Data[(int)offset + i] = data[dataOffset + i];

        node.Size = (ulong)node.Data.Count;
        TouchModify(node);
        return count;
    }

    /// <summary>Returns attributes for a handle.</summary>
    public NfsFileAttributes GetAttributes(NfsFileHandle handle)
    {
        var node = GetNode(handle);
        return ToAttributes(node);
    }

    /// <summary>Applies mutable attributes.</summary>
    public void SetAttributes(NfsFileHandle handle, NfsSetAttributes attrs)
    {
        var node = GetNode(handle);
        if (attrs.Mode.HasValue) node.Mode = attrs.Mode.Value;
        if (attrs.Size.HasValue)
        {
            if (node.Type != NfsFileType.Regular)
                throw new NfsException(NfsStatus.Inval, "Size can only be set on regular files.");

            int newSize = checked((int)attrs.Size.Value);
            if (newSize < node.Data.Count)
                node.Data.RemoveRange(newSize, node.Data.Count - newSize);
            else if (newSize > node.Data.Count)
                while (node.Data.Count < newSize) node.Data.Add(0);

            node.Size = (ulong)node.Data.Count;
            // Content changed: update mtime unless the caller is setting it explicitly.
            if (!attrs.ModifyTime.HasValue)
                node.ModifyTime = DateTimeOffset.UtcNow;
        }
        // Apply explicit timestamps. AccessTime always comes from the caller when set;
        // ModifyTime is already handled above for size changes.
        if (attrs.AccessTime.HasValue) node.AccessTime = attrs.AccessTime.Value;
        if (attrs.ModifyTime.HasValue) node.ModifyTime = attrs.ModifyTime.Value;
        // Metadata changed: always bump ctime.
        node.ChangeTime = DateTimeOffset.UtcNow;
    }

    /// <summary>Looks up an entry by name in a directory.</summary>
    public (NfsFileHandle Handle, NfsFileAttributes Attributes) Lookup(NfsFileHandle dir, string name)
    {
        string dirKey = Key(dir);
        EnsureDirectory(dirKey);
        if (!_directories[dirKey].TryGetValue(name, out string? childKey))
            throw new NfsException(NfsStatus.NoEnt, $"'{name}' not found.");

        return (FromKey(childKey), ToAttributes(_nodes[childKey]));
    }

    /// <summary>Creates a file in a directory.</summary>
    public NfsFileHandle CreateFile(NfsFileHandle dir, string name, NfsSetAttributes attrs, byte[]? initialData = null)
    {
        string dirKey = Key(dir);
        EnsureDirectory(dirKey);
        if (_directories[dirKey].ContainsKey(name))
            throw new NfsException(NfsStatus.Perm, $"'{name}' already exists.");

        string fileKey = Key(NewHandle());
        var data = initialData ?? Array.Empty<byte>();
        _nodes[fileKey] = new Node
        {
            Type = NfsFileType.Regular,
            Data = new List<byte>(data),
            Size = (ulong)data.Length,
            Mode = attrs.Mode ?? 0b110_100_100,
            AccessTime = DateTimeOffset.UtcNow,
            ModifyTime = DateTimeOffset.UtcNow,
            ChangeTime = DateTimeOffset.UtcNow,
        };
        _directories[dirKey][name] = fileKey;
        TouchModify(_nodes[dirKey]);
        return FromKey(fileKey);
    }

    /// <summary>Creates a directory in a directory.</summary>
    public NfsFileHandle MkDir(NfsFileHandle dir, string name, NfsSetAttributes attrs)
    {
        string dirKey = Key(dir);
        EnsureDirectory(dirKey);
        if (_directories[dirKey].ContainsKey(name))
            throw new NfsException(NfsStatus.Perm, $"'{name}' already exists.");

        string childKey = Key(NewHandle());
        _nodes[childKey] = new Node
        {
            Type = NfsFileType.Directory,
            Mode = attrs.Mode ?? 0b111_101_101,
            AccessTime = DateTimeOffset.UtcNow,
            ModifyTime = DateTimeOffset.UtcNow,
            ChangeTime = DateTimeOffset.UtcNow,
        };
        _directories[childKey] = new Dictionary<string, string>(StringComparer.Ordinal);
        _directories[dirKey][name] = childKey;
        TouchModify(_nodes[dirKey]);
        return FromKey(childKey);
    }

    /// <summary>Creates a symbolic link in a directory.</summary>
    public void SymLink(NfsFileHandle dir, string name, string linkTarget, NfsSetAttributes attrs)
    {
        string dirKey = Key(dir);
        EnsureDirectory(dirKey);
        if (_directories[dirKey].ContainsKey(name))
            throw new NfsException(NfsStatus.Perm, $"'{name}' already exists.");

        string childKey = Key(NewHandle());
        _nodes[childKey] = new Node
        {
            Type = NfsFileType.SymbolicLink,
            LinkTarget = linkTarget,
            Mode = attrs.Mode ?? 0b111_111_111,
            AccessTime = DateTimeOffset.UtcNow,
            ModifyTime = DateTimeOffset.UtcNow,
            ChangeTime = DateTimeOffset.UtcNow,
        };
        _directories[dirKey][name] = childKey;
        TouchModify(_nodes[dirKey]);
    }

    /// <summary>Lists entries in a directory.</summary>
    public IReadOnlyList<NfsDirectoryEntry> ReadDir(NfsFileHandle dir)
    {
        string dirKey = Key(dir);
        EnsureDirectory(dirKey);
        ulong cookie = 1;
        var result = new List<NfsDirectoryEntry>();
        foreach (var entry in _directories[dirKey].OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var child = _nodes[entry.Value];
            result.Add(new NfsDirectoryEntry
            {
                Name = entry.Key,
                Cookie = cookie++,
                FileHandle = FromKey(entry.Value),
                Attributes = ToAttributes(child),
            });
        }
        TouchAccess(_nodes[dirKey]);
        return result;
    }

    /// <summary>Reads a symbolic link target.</summary>
    public string ReadLink(NfsFileHandle handle)
    {
        var node = GetNode(handle);
        if (node.Type != NfsFileType.SymbolicLink)
            throw new NfsException(NfsStatus.Inval, "Handle is not a symbolic link.");
        TouchAccess(node);
        return node.LinkTarget ?? string.Empty;
    }

    /// <summary>Removes a file or symlink by name.</summary>
    public void Remove(NfsFileHandle dir, string name)
    {
        string dirKey = Key(dir);
        EnsureDirectory(dirKey);
        if (!_directories[dirKey].TryGetValue(name, out string? childKey))
            throw new NfsException(NfsStatus.NoEnt, $"'{name}' not found.");

        if (_nodes[childKey].Type == NfsFileType.Directory)
            throw new NfsException(NfsStatus.IsDir, "Entry is a directory.");

        _directories[dirKey].Remove(name);
        PruneNodeIfUnlinked(childKey);
        TouchModify(_nodes[dirKey]);
    }

    /// <summary>Removes an empty directory by name.</summary>
    public void RmDir(NfsFileHandle dir, string name)
    {
        string dirKey = Key(dir);
        EnsureDirectory(dirKey);
        if (!_directories[dirKey].TryGetValue(name, out string? childKey))
            throw new NfsException(NfsStatus.NoEnt, $"'{name}' not found.");

        EnsureDirectory(childKey);
        if (_directories[childKey].Count > 0)
            throw new NfsException(NfsStatus.NotSupp, "Directory is not empty.");

        _directories[dirKey].Remove(name);
        _directories.Remove(childKey);
        _nodes.Remove(childKey);
        TouchModify(_nodes[dirKey]);
    }

    /// <summary>Renames or moves an entry.</summary>
    public void Rename(NfsFileHandle fromDir, string fromName, NfsFileHandle toDir, string toName)
    {
        string fromDirKey = Key(fromDir);
        string toDirKey = Key(toDir);
        EnsureDirectory(fromDirKey);
        EnsureDirectory(toDirKey);

        if (!_directories[fromDirKey].TryGetValue(fromName, out string? childKey))
            throw new NfsException(NfsStatus.NoEnt, $"'{fromName}' not found.");

        _directories[fromDirKey].Remove(fromName);
        _directories[toDirKey][toName] = childKey;
        TouchModify(_nodes[fromDirKey]);
        if (!string.Equals(fromDirKey, toDirKey, StringComparison.Ordinal))
            TouchModify(_nodes[toDirKey]);
    }

    /// <summary>Creates an additional hard link to a file.</summary>
    public void Link(NfsFileHandle file, NfsFileHandle linkDir, string linkName)
    {
        string fileKey = Key(file);
        string linkDirKey = Key(linkDir);
        EnsureDirectory(linkDirKey);
        var node = GetNode(file);
        if (node.Type == NfsFileType.Directory)
            throw new NfsException(NfsStatus.IsDir, "Cannot hard-link a directory.");

        _directories[linkDirKey][linkName] = fileKey;
        node.LinkCount++;
        TouchModify(_nodes[linkDirKey]);
    }

    /// <summary>Returns simple filesystem statistics.</summary>
    public NfsFsStat FsStat()
    {
        ulong used = (ulong)_nodes.Values.Where(n => n.Type == NfsFileType.Regular).Sum(n => n.Data.Count);
        const ulong total = 1024UL * 1024UL * 1024UL;
        ulong free = total > used ? total - used : 0;
        return new NfsFsStat
        {
            TotalBytes = total,
            FreeBytes = free,
            AvailBytes = free,
            TotalFiles = (ulong)_nodes.Count,
            FreeFiles = ulong.MaxValue,
            AvailFiles = ulong.MaxValue,
        };
    }

    private static void TouchAccess(Node node) => node.AccessTime = DateTimeOffset.UtcNow;

    private static void TouchModify(Node node)
    {
        node.ModifyTime = DateTimeOffset.UtcNow;
        node.ChangeTime = node.ModifyTime;
    }

    private Node GetNode(NfsFileHandle handle)
    {
        string key = Key(handle);
        if (!_nodes.TryGetValue(key, out Node? node))
            throw new NfsException(NfsStatus.NoEnt, "Unknown file handle.");
        return node;
    }

    private Node GetFileNode(NfsFileHandle handle)
    {
        var node = GetNode(handle);
        if (node.Type != NfsFileType.Regular)
            throw new NfsException(NfsStatus.IsDir, "Handle is not a regular file.");
        return node;
    }

    private void EnsureDirectory(string key)
    {
        if (!_nodes.TryGetValue(key, out Node? node) || node.Type != NfsFileType.Directory)
            throw new NfsException(NfsStatus.NotDir, "Handle is not a directory.");
    }

    private void PruneNodeIfUnlinked(string key)
    {
        bool referenced = _directories.Values.Any(map => map.Values.Contains(key, StringComparer.Ordinal));
        if (!referenced)
        {
            if (_nodes[key].Type == NfsFileType.Directory)
                _directories.Remove(key);
            _nodes.Remove(key);
        }
    }

    private static NfsFileAttributes ToAttributes(Node node) => new()
    {
        Type = node.Type,
        Size = node.Size,
        Mode = node.Mode,
        NLink = node.LinkCount,
        Uid = 0,
        Gid = 0,
        Used = node.Size,
        Rdev = 0,
        FsId = 1,
        FileId = 0,
        AccessTime = node.AccessTime,
        ModifyTime = node.ModifyTime,
        ChangeTime = node.ChangeTime,
    };

    private static NfsFileHandle NewHandle() => new(Guid.NewGuid().ToByteArray());

    private static string Key(NfsFileHandle handle) => Convert.ToHexString(handle.Data);

    private static NfsFileHandle FromKey(string key) => new(Convert.FromHexString(key));

    private sealed class Node
    {
        public NfsFileType Type { get; init; }
        public List<byte> Data { get; init; } = new();
        public string? LinkTarget { get; init; }
        public ulong Size { get; set; }
        public uint Mode { get; set; }
        public uint LinkCount { get; set; } = 1;
        public DateTimeOffset AccessTime { get; set; }
        public DateTimeOffset ModifyTime { get; set; }
        public DateTimeOffset ChangeTime { get; set; }
    }
}
