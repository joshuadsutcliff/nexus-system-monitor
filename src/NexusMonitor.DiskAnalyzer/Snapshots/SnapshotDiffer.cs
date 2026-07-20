namespace NexusMonitor.DiskAnalyzer.Snapshots;

/// <summary>
/// Two-tree diff (spec §5). Walks both snapshot trees simultaneously, matching
/// children by name using the platform rule. Diffs logical size only. Unchanged
/// nodes are excluded from output. With mismatched thresholds, files below the
/// effective (max) threshold are re-aggregated into the small-files rollup so
/// the two sides compare at equal resolution.
/// </summary>
public static class SnapshotDiffer
{
    public static DiffResult Diff(ISnapshotStore store, long olderId, long newerId)
    {
        var older = store.GetSnapshot(olderId)
            ?? throw new InvalidOperationException($"Snapshot {olderId} not found.");
        var newer = store.GetSnapshot(newerId)
            ?? throw new InvalidOperationException($"Snapshot {newerId} not found.");
        if (!string.Equals(older.RootKey, newer.RootKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Snapshots have different roots ('{older.RootPath}' vs '{newer.RootPath}').");

        var threshold = Math.Max(older.ThresholdBytes, newer.ThresholdBytes);
        var oldSide = Sides.Build(store.LoadNodes(olderId), threshold);
        var newSide = Sides.Build(store.LoadNodes(newerId), threshold);

        var root = DiffDir(oldSide, oldSide.Root, newSide, newSide.Root, threshold);
        root ??= new DiffNode
        {
            Name = newSide.Root.Name, IsDirectory = true, Kind = ChangeKind.Unchanged,
            SizeBefore = oldSide.Root.Size, SizeAfter = newSide.Root.Size,
        };

        return new DiffResult
        {
            Older = older, Newer = newer, Root = root,
            EffectiveThreshold = threshold,
            ThresholdMismatch = older.ThresholdBytes != newer.ThresholdBytes,
            NamesMatchedCaseInsensitively = PathKeys.NamesAreCaseInsensitive,
        };
    }

    /// <summary>One snapshot side: node lookup + children-by-parent, re-aggregated to the effective threshold.</summary>
    private sealed class Sides
    {
        public SnapshotNode Root = null!;
        public ILookup<long, SnapshotNode> ChildrenByParent = null!;
        public Dictionary<long, (long Size, long Count)> ExtraSmall = new(); // re-aggregated per dir id

        public static Sides Build(IReadOnlyList<SnapshotNode> nodes, long effectiveThreshold)
        {
            var s = new Sides
            {
                Root = nodes.Single(n => n.ParentId == null),
                ChildrenByParent = nodes.Where(n => n.ParentId != null)
                                        .ToLookup(n => n.ParentId!.Value),
            };
            foreach (var n in nodes)
            {
                if (!n.IsDirectory && n.Size < effectiveThreshold && n.ParentId is long pid)
                {
                    var cur = s.ExtraSmall.TryGetValue(pid, out var v) ? v : (0L, 0L);
                    s.ExtraSmall[pid] = (cur.Item1 + n.Size, cur.Item2 + 1);
                }
            }
            return s;
        }

        public IEnumerable<SnapshotNode> VisibleChildren(SnapshotNode dir, long effectiveThreshold) =>
            ChildrenByParent[dir.Id].Where(c => c.IsDirectory || c.Size >= effectiveThreshold);

        public long SmallSize(SnapshotNode dir) =>
            dir.SmallFilesSize + (ExtraSmall.TryGetValue(dir.Id, out var v) ? v.Size : 0);
    }

    /// <summary>Returns null when the subtree is entirely unchanged.</summary>
    private static DiffNode? DiffDir(
        Sides oldSide, SnapshotNode oldDir, Sides newSide, SnapshotNode newDir, long effectiveThreshold)
    {
        var cmp = PathKeys.NameComparer;
        var oldKids = oldSide.VisibleChildren(oldDir, effectiveThreshold).ToDictionary(c => c.Name, cmp);
        var newKids = newSide.VisibleChildren(newDir, effectiveThreshold).ToDictionary(c => c.Name, cmp);

        var children = new List<DiffNode>();

        foreach (var (name, oldChild) in oldKids)
        {
            if (newKids.TryGetValue(name, out var newChild) && oldChild.IsDirectory == newChild.IsDirectory)
            {
                if (oldChild.IsDirectory)
                {
                    var sub = DiffDir(oldSide, oldChild, newSide, newChild, effectiveThreshold);
                    if (sub != null) children.Add(sub);
                }
                else if (newChild.Size != oldChild.Size)
                {
                    children.Add(Leaf(name, false,
                        newChild.Size > oldChild.Size ? ChangeKind.Grown : ChangeKind.Shrunk,
                        oldChild.Size, newChild.Size));
                }
                // equal size => Unchanged => excluded
            }
            else
            {
                children.Add(Leaf(name, oldChild.IsDirectory, ChangeKind.Removed, oldChild.Size, null));
            }
        }
        foreach (var (name, newChild) in newKids)
        {
            if (!oldKids.TryGetValue(name, out var oldMatch) || oldMatch.IsDirectory != newChild.IsDirectory)
                children.Add(Leaf(name, newChild.IsDirectory, ChangeKind.Added, null, newChild.Size));
            // (a type-flip already emitted its Removed in the oldKids loop above)
        }

        var smallDelta = newSide.SmallSize(newDir) - oldSide.SmallSize(oldDir);
        var sizeDelta = newDir.Size - oldDir.Size;
        if (children.Count == 0 && smallDelta == 0 && sizeDelta == 0)
            return null; // entirely unchanged subtree

        children.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));

        var dirNode = new DiffNode
        {
            Name = newDir.Name,
            IsDirectory = true,
            Kind = sizeDelta > 0 ? ChangeKind.Grown
                 : sizeDelta < 0 ? ChangeKind.Shrunk
                 : ChangeKind.Unchanged,
            SizeBefore = oldDir.Size,
            SizeAfter = newDir.Size,
            Delta = sizeDelta,
            SmallFilesDelta = smallDelta,
        };
        dirNode.Children.AddRange(children);
        return dirNode;
    }

    private static DiffNode Leaf(string name, bool isDir, ChangeKind kind, long? before, long? after) => new()
    {
        Name = name, IsDirectory = isDir, Kind = kind,
        SizeBefore = before, SizeAfter = after,
        Delta = (after ?? 0) - (before ?? 0),
    };
}
