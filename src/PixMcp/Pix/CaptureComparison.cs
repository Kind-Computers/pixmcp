using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixMcp.Pix;

/// <summary>How a comparison matches, normalizes and rolls up; the defaults keep conservative matching.</summary>
public sealed record ComparisonOptions(bool OrdinalMatching = false, bool IncludeSoftFields = false, int RollupDepth = 2, int TopChanges = 10,
    bool ShaderCodeDiff = true, bool SameHandle = false, int Repeats = 1, string RepeatMethod = "single");

/// <summary>Conservative matching over detached data; numeric event identities never cross captures.</summary>
public static class CaptureComparison
{
    public static ComparisonResultDto Compare(ComparisonSnapshot baseline, ComparisonSnapshot candidate,
        QueuePair[]? queuePairs = null, EventPair[]? eventPairs = null, ComparisonOptions? options = null)
    {
        ComparisonOptions opts = options ?? new();
        var queueMatches = new List<(ComparisonQueue A, ComparisonQueue B)>();
        var usedA = new HashSet<int>(); var usedB = new HashSet<int>();
        var ambiguities = new List<MatchAmbiguity>();
        foreach (QueuePair pair in queuePairs ?? [])
        {
            var a = baseline.Queues.SingleOrDefault(q => q.Index == pair.BaselineQueueIndex)
                ?? throw new ArgumentException("An explicit baseline queue does not exist.");
            var b = candidate.Queues.SingleOrDefault(q => q.Index == pair.CandidateQueueIndex)
                ?? throw new ArgumentException("An explicit candidate queue does not exist.");
            if (!usedA.Add(a.Index) || !usedB.Add(b.Index)) throw new ArgumentException("Queue pairs must be one-to-one.");
            queueMatches.Add((a, b));
        }
        void MatchQueues(Func<ComparisonQueue, string?> key)
        {
            var aa = baseline.Queues.Where(q => !usedA.Contains(q.Index)).GroupBy(key).Where(g => g.Key is not null).ToArray();
            var bb = candidate.Queues.Where(q => !usedB.Contains(q.Index)).GroupBy(key).ToDictionary(g => g.Key ?? "", g => g.ToArray());
            foreach (var group in aa)
                if (group.Count() == 1 && bb.TryGetValue(group.Key!, out var other) && other.Length == 1)
                { var a = group.Single(); var b = other[0]; usedA.Add(a.Index); usedB.Add(b.Index); queueMatches.Add((a, b)); }
        }
        MatchQueues(q => string.IsNullOrWhiteSpace(q.Name) ? null : Json.Serialize(new[] { q.Type, q.Name }));
        MatchQueues(q => q.Type);
        var matchedA = new HashSet<EventRef>(); var matchedB = new HashSet<EventRef>();
        var paired = new List<(ComparisonEvent A, ComparisonEvent B, string Method)>();
        foreach (EventPair pair in eventPairs ?? [])
        {
            var a = baseline.Queues.SelectMany(q => q.Events).SingleOrDefault(e => e.EventRef == pair.Baseline)
                ?? throw new ArgumentException("An explicit baseline event does not exist.");
            var b = candidate.Queues.SelectMany(q => q.Events).SingleOrDefault(e => e.EventRef == pair.Candidate)
                ?? throw new ArgumentException("An explicit candidate event does not exist.");
            if (!matchedA.Add(a.EventRef) || !matchedB.Add(b.EventRef)) throw new ArgumentException("Event pairs must be one-to-one.");
            paired.Add((a, b, "explicit"));
        }
        foreach (var (a, b) in queueMatches)
        {
            string Key(ComparisonEvent e) => Json.Serialize(new { e.MarkerPath, e.Name, e.Kind, e.IsMarker });
            // A repeated ancestor path does not identify which occurrence contains a draw.
            // Even a unique child name/hash cannot establish that parent correspondence.
            var repeatedPaths = RepeatedMarkerPaths(a).Concat(RepeatedMarkerPaths(b)).ToArray();
            bool HasAmbiguousPath(ComparisonEvent e)
            {
                string[] path = e.IsMarker ? [.. e.MarkerPath, e.Name] : e.MarkerPath;
                return repeatedPaths.Any(prefix => prefix.Length <= path.Length && path.Take(prefix.Length).SequenceEqual(prefix));
            }
            var groupsB = b.Events.Where(e => !matchedB.Contains(e.EventRef)).GroupBy(Key).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var ga in a.Events.Where(e => !matchedA.Contains(e.EventRef)).GroupBy(Key))
            {
                if (!groupsB.TryGetValue(ga.Key, out var gb)) continue;
                ComparisonEvent[] left = ga.ToArray();
                if (left.Any(HasAmbiguousPath) || gb.Any(HasAmbiguousPath))
                {
                    // With ordinal matching, equal occurrence counts pair by order (confidence low); otherwise the path stays ambiguous.
                    if (opts.OrdinalMatching && left.Length == gb.Length) { PairInOrder(left, gb); continue; }
                    ambiguities.Add(new("markerPaths", ga.Key, left.Select(e => e.EventRef).ToArray(), gb.Select(e => e.EventRef).ToArray()));
                    continue;
                }
                if (left.Length == 1 && gb.Length == 1)
                { Add(left[0], gb[0], left[0].IsMarker ? "markerPath" : "uniqueWorkInMarkerPath"); continue; }
                foreach (var hashGroup in left.Where(e => !e.IsMarker && e.ShaderKey is not null).GroupBy(e => e.ShaderKey))
                {
                    var right = gb.Where(e => !e.IsMarker && e.ShaderKey == hashGroup.Key && !matchedB.Contains(e.EventRef)).ToArray();
                    if (hashGroup.Count() == 1 && right.Length == 1) Add(hashGroup.Single(), right[0], "uniqueShaderWithinPath");
                }
                var remainingA = left.Where(e => !matchedA.Contains(e.EventRef)).ToArray();
                var remainingB = gb.Where(e => !matchedB.Contains(e.EventRef)).ToArray();
                if (opts.OrdinalMatching && remainingA.Length == remainingB.Length && remainingA.Length > 0) { PairInOrder(remainingA, remainingB); continue; }
                if (remainingA.Length > 0 && remainingB.Length > 0)
                    ambiguities.Add(new("events", ga.Key, remainingA.Select(e => e.EventRef).ToArray(), remainingB.Select(e => e.EventRef).ToArray()));
            }
        }
        foreach (var group in baseline.Queues.Where(q => !usedA.Contains(q.Index)).GroupBy(q => q.Type))
        {
            var right = candidate.Queues.Where(q => !usedB.Contains(q.Index) && q.Type == group.Key).ToArray();
            var remainingA = group.SelectMany(q => q.Events).Where(e => !matchedA.Contains(e.EventRef)).Select(e => e.EventRef).ToArray();
            var remainingB = right.SelectMany(q => q.Events).Where(e => !matchedB.Contains(e.EventRef)).Select(e => e.EventRef).ToArray();
            if (remainingA.Length > 0 && remainingB.Length > 0) ambiguities.Add(new("queues", group.Key, remainingA, remainingB));
        }
        var ambiguousA = ambiguities.SelectMany(a => a.Baseline).ToHashSet();
        var ambiguousB = ambiguities.SelectMany(a => a.Candidate).ToHashSet();
        var changes = new List<EventChange>();
        foreach (var (a, b, method) in paired)
        {
            decimal? delta = a.EopNs.HasValue && b.EopNs.HasValue ? (decimal)b.EopNs.Value - a.EopNs.Value : null;
            var fields = new List<FieldChange>();
            foreach (string section in a.Sections.Keys.Intersect(b.Sections.Keys).Order())
                Diff(section, "", a.Sections[section], b.Sections[section], fields);
            if ((delta.HasValue && delta.Value != 0) || a.EopNs != b.EopNs || fields.Count > 0)
                changes.Add(new EventChange(a.EventRef, b.EventRef, a.Name, a.MarkerPath, method, a.EopNs, b.EopNs,
                    delta, delta.HasValue ? Math.Round((double)delta.Value / 1e6, 3) : null,
                    a.EopNs is > 0 && delta.HasValue ? (double)(100 * delta.Value / a.EopNs.Value) : null,
                    a.Semantics, b.Semantics, fields)
                {
                    Kind = a.Kind, Confidence = Confidence(method), BaselineSpreadNs = a.SpreadNs, CandidateSpreadNs = b.SpreadNs,
                    BelowNoiseFloor = delta.HasValue && a.SpreadNs.HasValue && b.SpreadNs.HasValue && Math.Abs(delta.Value) <= Math.Max(a.SpreadNs.Value, b.SpreadNs.Value),
                });
        }
        EventChange[] sorted = changes.OrderByDescending(c => Math.Abs(c.DeltaNs ?? 0)).ThenBy(c => c.Baseline.QueueIndex).ThenBy(c => c.Baseline.EventIndex).ToArray();
        ComparisonEvent[] baselineOnly = baseline.Queues.SelectMany(q => q.Events).Where(e => !matchedA.Contains(e.EventRef) && !ambiguousA.Contains(e.EventRef)).ToArray();
        ComparisonEvent[] candidateOnly = candidate.Queues.SelectMany(q => q.Events).Where(e => !matchedB.Contains(e.EventRef) && !ambiguousB.Contains(e.EventRef)).ToArray();
        (ComparisonEvent A, ComparisonEvent B)[] timedWork = paired
            .Where(p => !p.A.IsMarker && ComparisonRollup.IsWork(p.A.Kind) && p.A.EopNs.HasValue && p.B.EopNs.HasValue).Select(p => (p.A, p.B)).ToArray();
        ComparisonNoiseDto noise = ComparisonRollup.Noise(opts.Repeats, opts.RepeatMethod,
            timedWork.Where(p => p.A.SpreadNs.HasValue && p.B.SpreadNs.HasValue).Select(p => Math.Max(p.A.SpreadNs!.Value, p.B.SpreadNs!.Value)));
        IReadOnlyList<ComparisonWarningDto> warnings = ComparisonRollup.ProvenanceWarnings(baseline.Provenance, candidate.Provenance, opts.SameHandle);
        return new ComparisonResultDto(baseline.Handle, candidate.Handle, paired.Count, sorted,
            baselineOnly.Select(e => e.EventRef).ToArray(), candidateOnly.Select(e => e.EventRef).ToArray(),
            ambiguities, baseline.Coverage.Concat(candidate.Coverage).ToArray(), baseline.Provenance, candidate.Provenance)
        {
            Totals = ComparisonRollup.Totals(baseline, candidate, queueMatches, sorted, baselineOnly, candidateOnly, opts.TopChanges),
            ByMarkerPath = ComparisonRollup.ByMarkerPath(timedWork, opts.RollupDepth, noise.NoiseFloorNs),
            Noise = noise,
            Warnings = warnings,
            ProvenanceMismatch = warnings.Any(w => w.Severity == "warning"),
            CodeDiffs = opts.ShaderCodeDiff ? ComparisonRollup.CodeDiffs(paired.Select(p => (p.A, p.B)), baseline.HlslByHash, candidate.HlslByHash) : [],
            QueueMatches = queueMatches.Select(q => new QueuePair(q.A.Index, q.B.Index)).ToArray(),
        };

        void Add(ComparisonEvent a, ComparisonEvent b, string method)
        { matchedA.Add(a.EventRef); matchedB.Add(b.EventRef); paired.Add((a, b, method)); }

        void PairInOrder(IEnumerable<ComparisonEvent> left, IEnumerable<ComparisonEvent> right)
        {
            foreach (var (a, b) in left.OrderBy(e => e.EventRef.EventIndex).Zip(right.OrderBy(e => e.EventRef.EventIndex))) Add(a, b, "ordinalWithinPath");
        }

        static IEnumerable<string[]> RepeatedMarkerPaths(ComparisonQueue queue)
            => queue.Events.Where(e => e.IsMarker).GroupBy(e => Json.Serialize(new { e.MarkerPath, e.Name }))
                .Where(g => g.Count() > 1).Select(g => g.First().MarkerPath.Append(g.First().Name).ToArray());
    }

    /// <summary>high for explicit pairs and unique paths, medium for a unique shader within a path, low for order within a path.</summary>
    public static string Confidence(string matchMethod) => matchMethod switch
    {
        "uniqueShaderWithinPath" => "medium",
        "ordinalWithinPath" => "low",
        _ => "high",
    };

    private static readonly HashSet<string> IdentityFields = new(StringComparer.OrdinalIgnoreCase)
    { "id", "apiObjectId", "heapId", "address", "gpuVirtualAddress", "eventRef", "resourceRef", "shaderRef", "markerPath", "event", "index",
      "bindingCount", "bindingOffset", "bindingCountReturned", "nextBindingOffset", "bindingsTruncated", "nextCalls" };

    /// <summary>Fields that change without a behaviour change (descriptor heap slots, which evidence found a binding); compared only on request.</summary>
    private static readonly HashSet<string> SoftFields = new(StringComparer.OrdinalIgnoreCase) { "eventSource", "descriptorHeapIndex" };

    /// <summary>Drops capture identities (and soft fields unless requested), sorts object keys, and orders bindings arrays as sets.</summary>
    public static JsonElement Normalize(object value, bool includeSoftFields = false)
    {
        JsonNode? Visit(JsonNode? node, bool preserveNames, bool unordered) => node switch
        {
            JsonObject obj => new JsonObject(obj.Where(p => preserveNames || !(IdentityFields.Contains(p.Key) || (!includeSoftFields && SoftFields.Contains(p.Key))))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Visit(p.Value, preserveNames || p.Key.Equals("defines", StringComparison.OrdinalIgnoreCase),
                    p.Key.Equals("bindings", StringComparison.OrdinalIgnoreCase))))),
            JsonArray array => Ordered(array.Select(child => Visit(child, preserveNames, false)).ToArray(), unordered),
            _ => node?.DeepClone(),
        };
        static JsonArray Ordered(JsonNode?[] children, bool unordered)
            => new(unordered ? children.OrderBy(c => c?.ToJsonString() ?? "null", StringComparer.Ordinal).ToArray() : children);
        return JsonSerializer.SerializeToElement(Visit(JsonSerializer.SerializeToNode(value, Json.Options), false, false), Json.Options);
    }

    public static JsonElement[] NormalizeSet(IEnumerable<object> values, bool includeSoftFields = false)
        => values.Select(v => Normalize(v, includeSoftFields)).OrderBy(value => value.GetRawText(), StringComparer.Ordinal).ToArray();

    /// <summary>Recurses objects by key and equal-length arrays by position, so a rebinding reports its field path.</summary>
    private static void Diff(string section, string path, JsonElement? a, JsonElement? b, List<FieldChange> changes)
    {
        if (IsUnavailable(a) || IsUnavailable(b)) return;
        if (a.HasValue && b.HasValue && JsonElement.DeepEquals(a.Value, b.Value)) return;
        if (a?.ValueKind == JsonValueKind.Object && b?.ValueKind == JsonValueKind.Object)
        {
            var aa = a.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var bb = b.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            foreach (string name in aa.Keys.Union(bb.Keys).Order(StringComparer.Ordinal))
                Diff(section, path + "/" + name.Replace("~", "~0").Replace("/", "~1"),
                    aa.TryGetValue(name, out var av) ? av : null, bb.TryGetValue(name, out var bv) ? bv : null, changes);
        }
        else if (a?.ValueKind == JsonValueKind.Array && b?.ValueKind == JsonValueKind.Array && a.Value.GetArrayLength() == b.Value.GetArrayLength())
        {
            int index = 0;
            foreach (var (x, y) in a.Value.EnumerateArray().Zip(b.Value.EnumerateArray()))
                Diff(section, path + "/" + index++, x, y, changes);
        }
        else changes.Add(new(section, path, a, b));
    }

    private static bool IsUnavailable(JsonElement? value)
        => value?.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("unavailable", out var unavailable)
            && unavailable.ValueKind == JsonValueKind.True;
}
