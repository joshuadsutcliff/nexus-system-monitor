using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusMonitor.DiskAnalyzer.Models;

namespace NexusMonitor.DiskAnalyzer.Scanning;

/// <summary>
/// Near-instant NTFS scanner that reads the Master File Table (MFT) directly from
/// the volume, bypassing the Windows filesystem layer entirely.
/// Falls back to <see cref="RecursiveScanner"/> for non-NTFS volumes, network paths,
/// and non-Windows operating systems.
///
/// Algorithm (MFT direct-read):
///   1. Open volume with GENERIC_READ (no filesystem traversal)
///   2. Query NTFS volume parameters (MFT cluster offset, record size, etc.)
///   3. Read the raw MFT in large sequential chunks
///   4. Parse each 1 024-byte FILE record: name, parent FRN, size, timestamps
///   5. Build the DiskNode tree in memory from FRN → parent mappings
///
/// Typical scan time for a 500 GB NTFS drive: &lt;2 seconds.
/// </summary>
public sealed class MftScanner : IDiskScanner
{
    public async Task<ScanResult> ScanAsync(
        string path,
        ScanOptions? options,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        options ??= new ScanOptions();

        // Only available on Windows NTFS; fall back otherwise.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await new RecursiveScanner().ScanAsync(path, options, progress, ct);

        string fullPath = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string driveRoot = Path.GetPathRoot(fullPath) ?? fullPath;

        string fsType;
        long volTotal = 0, volFree = 0;
        try
        {
            var di = new DriveInfo(driveRoot);
            fsType   = di.DriveFormat;
            volTotal = di.TotalSize;
            volFree  = di.TotalFreeSpace;
        }
        catch { fsType = string.Empty; }

        if (!string.Equals(fsType, "NTFS", StringComparison.OrdinalIgnoreCase))
            return await new RecursiveScanner().ScanAsync(path, options, progress, ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Volume handle path: "\\.\C:" (no trailing backslash)
            string volumePath = @"\\.\" + driveRoot.TrimEnd('\\', '/');

#pragma warning disable CA1416 // OS guard is above; lambda captures the already-verified path
            var entries = await Task.Run(() => ReadMft(volumePath, progress, ct), ct);
#pragma warning restore CA1416
            var rootNode = BuildTree(entries, fullPath, driveRoot, options, ct);

            rootNode.Name = string.IsNullOrEmpty(rootNode.Name)
                ? driveRoot : rootNode.Name;

            sw.Stop();
            return new ScanResult
            {
                Root         = rootNode,
                ScannedPath  = path,
                Duration     = sw.Elapsed,
                TotalFiles   = rootNode.FileCount,
                TotalFolders = rootNode.FolderCount,
                TotalSize    = rootNode.Size,
                FileSystem   = fsType,
                VolumeTotal  = volTotal,
                VolumeFree   = volFree,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Log the native failure before falling back so the user can see what happened
            progress?.Report(new ScanProgress
            {
                CurrentPath = $"MFT scan failed ({ex.GetType().Name}: {ex.Message}); falling back to directory scan…",
            });
            return await new RecursiveScanner().ScanAsync(path, options, progress, ct);
        }
    }

    // ── MFT read pass ─────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static unsafe List<MftEntry> ReadMft(
        string volumePath,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        const uint GENERIC_READ     = 0x80000000u;
        const uint FILE_SHARE_RW    = 0x00000003u;
        const uint OPEN_EXISTING    = 3;
        const uint FLAGS_FAST       = 0x20000000u | 0x08000000u; // NO_BUFFERING | SEQUENTIAL_SCAN
        const uint FSCTL_NTFS_DATA  = 0x00090064u;
        const uint FILE_BEGIN       = 0;
        const int  CHUNK_SIZE       = 4 * 1024 * 1024; // 4 MB read chunks

        nint hVol = NativeMethods.CreateFileW(
            volumePath, GENERIC_READ, FILE_SHARE_RW, nint.Zero,
            OPEN_EXISTING, FLAGS_FAST, nint.Zero);

        if (hVol == new nint(-1))
            throw new Exception($"Cannot open volume {volumePath} (error {Marshal.GetLastWin32Error()})");

        try
        {
            // ── 1. Get NTFS volume parameters ──────────────────────────────────
            NtfsVolumeData vol;
            uint returned;
            bool ok = NativeMethods.DeviceIoControl(
                hVol, FSCTL_NTFS_DATA, nint.Zero, 0,
                (nint)(&vol), (uint)sizeof(NtfsVolumeData), out returned, nint.Zero);

            if (!ok || returned < sizeof(NtfsVolumeData))
                throw new Exception("FSCTL_GET_NTFS_VOLUME_DATA failed");

            long recSize   = vol.BytesPerFileRecordSegment;
            long mftOffset = vol.MftStartLcn * vol.BytesPerCluster;
            long mftLength = vol.MftValidDataLength;
            long sector    = vol.BytesPerSector;

            // Read chunk size must be sector-aligned; CHUNK_SIZE already is (4 MB).
            // Allocate a sector-aligned buffer via HGlobal (returns page-aligned memory).
            nint bufPtr = Marshal.AllocHGlobal(CHUNK_SIZE);
            try
            {
                var entries = new List<MftEntry>((int)Math.Min(mftLength / recSize + 1, 16_000_000));
                long bytesProcessed = 0;
                long filesFound     = 0;

                // Seek to MFT start (already sector-aligned: LCN * BytesPerCluster)
                NativeMethods.SetFilePointerEx(hVol, mftOffset, out _, FILE_BEGIN);

                while (bytesProcessed < mftLength)
                {
                    ct.ThrowIfCancellationRequested();

                    long remaining = mftLength - bytesProcessed;
                    // Round up read size to sector alignment
                    long wantRaw   = Math.Min(CHUNK_SIZE, remaining);
                    uint toRead    = (uint)(((wantRaw - 1) / sector + 1) * sector);
                    if (toRead > CHUNK_SIZE) toRead = CHUNK_SIZE;

                    if (!NativeMethods.ReadFile(hVol, bufPtr, toRead, out uint nRead, nint.Zero) || nRead == 0)
                        break;

                    // Parse MFT records within this chunk
                    byte* chunk = (byte*)bufPtr;
                    for (long off = 0; off < nRead && bytesProcessed + off < mftLength; off += recSize)
                    {
                        byte* rec = chunk + off;
                        // Magic: "FILE" = 0x454C4946
                        if (*(uint*)rec != 0x454C4946u) continue;

                        ulong recordNumber = (ulong)((bytesProcessed + off) / recSize);
                        var entry = ParseRecord(rec, recSize, recordNumber);
                        if (entry.HasValue)
                        {
                            entries.Add(entry.Value);
                            filesFound++;
                            if (filesFound % 50_000 == 0)
                                progress?.Report(new ScanProgress
                                {
                                    FilesScanned = filesFound,
                                    BytesCounted = bytesProcessed,
                                    CurrentPath  = "Reading Master File Table…",
                                });
                        }
                    }

                    bytesProcessed += nRead;
                }

                return entries;
            }
            finally
            {
                Marshal.FreeHGlobal(bufPtr);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hVol);
        }
    }

    // ── MFT record parser ─────────────────────────────────────────────────────

    private static unsafe MftEntry? ParseRecord(byte* rec, long recSize, ulong recordNumber)
    {
        // FILE record header offsets:
        //   0  : magic (DWORD)
        //   4  : update-sequence offset (WORD)
        //   6  : update-sequence size in words (WORD)
        //  20  : first attribute offset (WORD)
        //  22  : flags (WORD): 0x01=InUse, 0x02=IsDirectory
        //  24  : bytes in use (DWORD)

        // Minimum header size: 28 bytes to read all required header fields
        if (recSize < 28) return null;

        ushort flags     = *(ushort*)(rec + 22);
        if ((flags & 0x01) == 0) return null;   // record not in use

        ushort updateSeqOff  = *(ushort*)(rec + 4);
        ushort updateSeqSize = *(ushort*)(rec + 6);
        ApplyFixup(rec, recSize, updateSeqOff, updateSeqSize);

        bool   isDir      = (flags & 0x02) != 0;
        uint   bytesInUse = *(uint*)(rec + 24);
        int    attrOff    = *(ushort*)(rec + 20);

        ulong  parentFrn    = 0;
        string? name        = null;
        long   size         = 0;
        long   allocSize    = 0;
        DateTime lastMod    = default;
        uint   fileAttribs  = 0;
        bool   gotWin32     = false;

        // Walk attribute headers
        while (attrOff >= 0 && (uint)attrOff + 8 < bytesInUse && (uint)attrOff + 8 < recSize)
        {
            uint attrType = *(uint*)(rec + attrOff);
            uint attrLen  = *(uint*)(rec + attrOff + 4);

            if (attrType == 0xFFFFFFFF) break;          // END_OF_ATTRIBUTES
            if (attrLen < 8 || attrOff + attrLen > recSize) break;

            byte nonResident = rec[attrOff + 8];

            // ── $FILE_NAME (0x30) ──────────────────────────────────────────────
            if (attrType == 0x30 && nonResident == 0)
            {
                // Resident attribute: value offset at rec+attrOff+20 (WORD)
                // Need attrOff+22 to be within record
                if ((uint)(attrOff + 22) <= (uint)recSize)
                {
                    ushort valOff = *(ushort*)(rec + attrOff + 20);
                    long fnStart  = (long)attrOff + valOff;

                    // $FILE_NAME layout:
                    //   0  : Parent FRN (ULONGLONG)
                    //  16  : Modified time (FILETIME)
                    //  40  : Allocated size (ULONGLONG)
                    //  48  : Real (data) size (ULONGLONG)
                    //  56  : File attributes (DWORD)
                    //  64  : File name length in chars (BYTE)
                    //  65  : Namespace (BYTE): 0=POSIX,1=Win32,2=DOS,3=Win32+DOS
                    //  66  : UTF-16 file name
                    if (fnStart >= 0 && fnStart + 66 <= recSize)
                    {
                        byte* fn      = rec + fnStart;
                        byte  nameLen = fn[64];
                        byte  nameNs  = fn[65]; // namespace

                        if (fnStart + 66 + nameLen * 2 <= recSize)
                        {
                            ulong parentRef   = *(ulong*)fn;
                            long  mftModTicks = *(long*)(fn + 16);
                            ulong allocSz     = *(ulong*)(fn + 40);
                            ulong dataSz      = *(ulong*)(fn + 48);
                            uint  attribs     = *(uint*)(fn + 56);

                            // Prefer Win32 (1) or Win32+DOS (3) over POSIX (0) or DOS-only (2)
                            bool isWin32 = nameNs == 1 || nameNs == 3;
                            if (name == null || (isWin32 && !gotWin32))
                            {
                                name        = new string((char*)(fn + 66), 0, nameLen);
                                parentFrn   = parentRef & 0x0000FFFFFFFFFFFF; // lower 48 bits
                                fileAttribs = attribs;
                                gotWin32    = isWin32;

                                // Use FILETIME (100-nanosecond intervals since Jan 1, 1601)
                                try { lastMod = DateTime.FromFileTimeUtc(mftModTicks); } catch { lastMod = default; }

                                // File size from $FILE_NAME (resident in the attribute)
                                if (dataSz > 0 && (long)dataSz < 0x00FFFFFFFFFFFFFF)
                                    size = (long)dataSz;
                                if (allocSz > 0 && (long)allocSz < 0x00FFFFFFFFFFFFFF)
                                    allocSize = (long)allocSz;
                            }
                        }
                    }
                }
            }
            // ── $DATA (0x80) — prefer this over $FILE_NAME for accurate size ──
            else if (attrType == 0x80 && !isDir)
            {
                // Only the unnamed $DATA stream (NameLength byte at offset 9)
                if ((uint)(attrOff + 10) <= (uint)recSize)
                {
                    byte dataNameLen = rec[attrOff + 9];
                    if (dataNameLen == 0)
                    {
                        if (nonResident == 0)
                        {
                            // Resident: ValueLength at offset 16 from attribute start
                            if ((uint)(attrOff + 20) <= (uint)recSize)
                            {
                                uint valLen = *(uint*)(rec + attrOff + 16);
                                size      = (int)valLen;
                                allocSize = (int)valLen;
                            }
                        }
                        else
                        {
                            // Non-resident: AllocatedSize at +40, DataSize at +48 from attr start
                            if ((uint)(attrOff + 56) <= (uint)recSize)
                            {
                                long allSz  = *(long*)(rec + attrOff + 40);
                                long dataSz = *(long*)(rec + attrOff + 48);
                                if (dataSz >= 0 && dataSz < 0x00FFFFFFFFFFFFFF) size      = dataSz;
                                if (allSz  >= 0 && allSz  < 0x00FFFFFFFFFFFFFF) allocSize = allSz;
                            }
                        }
                    }
                }
            }

            attrOff += (int)attrLen;
        }

        if (string.IsNullOrEmpty(name)) return null;

        // Skip the special MFT system files (FRN 0–11: $MFT, $MFTMirr, $LogFile, etc.)
        // and the root directory record (FRN 5) itself (added explicitly as root node)
        if (recordNumber < 12 && recordNumber != 5) return null;

        bool isSystem = (fileAttribs & 0x4) != 0;
        bool isHidden = (fileAttribs & 0x2) != 0;

        return new MftEntry(recordNumber, parentFrn, name, size, allocSize, lastMod, isDir, isSystem, isHidden);
    }

    // ── Update Sequence Array (USA) fixup ─────────────────────────────────────

    private static unsafe void ApplyFixup(byte* rec, long recSize, ushort seqOff, ushort seqSize)
    {
        if (seqOff < 1 || seqSize < 2) return;
        if (seqOff + (long)seqSize * 2 > recSize) return;  // sequence array out of bounds
        ushort* seq = (ushort*)(rec + seqOff);
        ushort  check = seq[0];
        for (int i = 1; i < seqSize; i++)
        {
            long sectorEndOff = (long)i * 512 - 2;
            if (sectorEndOff + 1 >= recSize) break;
            ushort* target = (ushort*)(rec + sectorEndOff);
            if (*target == check) *target = seq[i];
        }
    }

    // ── Build DiskNode tree ───────────────────────────────────────────────────

    /// <summary>The NTFS volume root is always MFT record 5.</summary>
    private const ulong NTFS_ROOT_FRN = 5;

    /// <summary>
    /// Walks <paramref name="parts"/> (path components below the volume root) down the
    /// MFT parent/child index. Returns false — with <paramref name="frn"/> reset to the
    /// volume root — when ANY component is missing (or matches only a non-directory):
    /// callers must treat that as "this scan cannot be served from the MFT" and fall
    /// back to a directory walk, never scan the deepest ancestor that did resolve.
    /// </summary>
    internal static bool TryResolvePathFrn(
        Dictionary<ulong, MftEntry> frnMap,
        Dictionary<ulong, List<ulong>> childrenOf,
        IReadOnlyList<string> parts,
        out ulong frn)
    {
        frn = NTFS_ROOT_FRN;
        foreach (var part in parts)
        {
            if (!childrenOf.TryGetValue(frn, out var kids)) { frn = NTFS_ROOT_FRN; return false; }
            ulong? next = null;
            foreach (var kid in kids)
            {
                if (frnMap.TryGetValue(kid, out var kEntry) &&
                    kEntry.IsDirectory &&
                    string.Equals(kEntry.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = kid;
                    break;
                }
            }
            if (next is null) { frn = NTFS_ROOT_FRN; return false; }
            frn = next.Value;
        }
        return true;
    }

    private static DiskNode BuildTree(
        List<MftEntry> entries,
        string requestedPath,
        string driveRoot,
        ScanOptions options,
        CancellationToken ct)
    {
        // FRN → entry lookup
        var frnMap = new Dictionary<ulong, MftEntry>(entries.Count);
        foreach (var e in entries)
            frnMap[e.Frn] = e;   // last-write wins on duplicate FRNs (shouldn't happen)

        // FRN → child FRNs
        var childrenOf = new Dictionary<ulong, List<ulong>>(entries.Count / 4);
        foreach (var e in entries)
        {
            if (!childrenOf.TryGetValue(e.ParentFrn, out var list))
                childrenOf[e.ParentFrn] = list = new List<ulong>(8);
            list.Add(e.Frn);
        }

        // Find FRN of the requested path. Every component must resolve — a partial
        // resolution must NOT scan the deepest ancestor that did resolve (that
        // silently returns volume-root-sized totals stamped with the requested
        // path, e.g. when the MFT's on-disk state doesn't yet contain a freshly
        // created directory). Throwing here lands in ScanAsync's catch, which
        // falls back to RecursiveScanner and reports the reason via progress.
        ulong targetFrn = NTFS_ROOT_FRN;
        string relativePart = requestedPath;
        if (relativePart.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))
            relativePart = relativePart.Substring(driveRoot.Length);

        if (!string.IsNullOrEmpty(relativePart))
        {
            var parts = relativePart.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            ct.ThrowIfCancellationRequested();
            if (!TryResolvePathFrn(frnMap, childrenOf, parts, out targetFrn))
                throw new DirectoryNotFoundException(
                    $"MFT index could not resolve '{requestedPath}' to a volume subtree.");
        }

        // ── Iterative DFS tree build — avoids stack overflow on deep hierarchies ──
        bool hasRootEntry = frnMap.TryGetValue(targetFrn, out var rootEntry);
        var rootNode = new DiskNode
        {
            Name         = hasRootEntry ? rootEntry.Name
                                        : Path.GetFileName(requestedPath.TrimEnd('\\')) ?? requestedPath,
            FullPath     = requestedPath,
            IsDirectory  = !hasRootEntry || rootEntry.IsDirectory,
            LastModified = hasRootEntry ? rootEntry.LastModified : default,
        };

        var stack           = new Stack<(ulong Frn, DiskNode Node)>(256);
        var visitOrder      = new List<DiskNode>(entries.Count); // pre-order; reversed → post-order
        var expandedDirFrns = new HashSet<ulong>();              // prevents cycles (root FRN==parentFRN, hard links, etc.)
        stack.Push((targetFrn, rootNode));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (nodeFrn, node) = stack.Pop();
            visitOrder.Add(node);

            if (!node.IsDirectory) continue;
            if (!expandedDirFrns.Add(nodeFrn)) continue;        // already expanded — skip to prevent cycles
            if (!childrenOf.TryGetValue(nodeFrn, out var childFrns)) continue;

            foreach (ulong childFrn in childFrns)
            {
                ct.ThrowIfCancellationRequested();
                if (!frnMap.TryGetValue(childFrn, out var childEntry)) continue;

                string childPath = Path.Combine(node.FullPath, childEntry.Name);
                if (options.ExcludedPaths.Contains(childPath)) continue;
                if (!childEntry.IsDirectory && childEntry.Size < options.MinFileSizeBytes) continue;

                var childNode = new DiskNode
                {
                    Name          = childEntry.Name,
                    FullPath      = childPath,
                    IsDirectory   = childEntry.IsDirectory,
                    LastModified  = childEntry.LastModified,
                    Parent        = node,
                    Size          = childEntry.IsDirectory ? 0 : childEntry.Size,
                    AllocatedSize = childEntry.IsDirectory ? 0 : childEntry.AllocatedSize,
                };
                node.Children.Add(childNode);
                stack.Push((childFrn, childNode));
            }
        }

        // Post-order pass: accumulate sizes bottom-up then sort children by size descending.
        // Processing visitOrder in reverse guarantees each child is processed before its parent.
        for (int i = visitOrder.Count - 1; i >= 0; i--)
        {
            var node = visitOrder[i];
            if (node.Children.Count > 1)
                node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));

            if (node.Parent is null) continue;
            var p = node.Parent;
            p.Size          += node.Size;
            p.AllocatedSize += node.AllocatedSize;
            if (node.IsDirectory)
            {
                p.FileCount   += node.FileCount;
                p.FolderCount += node.FolderCount + 1;
            }
            else
            {
                p.FileCount += 1;
            }
        }

        return rootNode;
    }

    // ── Native interop ────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            nint lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, nint hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DeviceIoControl(
            nint hDevice, uint dwIoControlCode,
            nint lpInBuffer, uint nInBufferSize,
            nint lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, nint lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetFilePointerEx(
            nint hFile, long liDistanceToMove,
            out long lpNewFilePointer, uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(
            nint hFile, nint lpBuffer,
            uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead,
            nint lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(nint hObject);
    }

    // ── NTFS_VOLUME_DATA_BUFFER (from FSCTL_GET_NTFS_VOLUME_DATA) ────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeData
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    // ── Internal record for a parsed MFT entry ────────────────────────────────

    internal readonly record struct MftEntry(
        ulong    Frn,
        ulong    ParentFrn,
        string   Name,
        long     Size,
        long     AllocatedSize,
        DateTime LastModified,
        bool     IsDirectory,
        bool     IsSystem,
        bool     IsHidden);
}
