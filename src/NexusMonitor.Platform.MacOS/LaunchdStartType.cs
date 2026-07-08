using System.Xml;
using System.Xml.Linq;
using NexusMonitor.Core.Models;

namespace NexusMonitor.Platform.MacOS;

/// <summary>
/// Facts extracted from a single launchd plist that are relevant to <see cref="ServiceStartType"/>
/// derivation. <see cref="Label"/> is the plist's own internal <c>Label</c> key, used only as a
/// fallback lookup key when a plist's filename doesn't match its label (see
/// <see cref="MacOSLaunchdIndex"/>).
/// </summary>
public readonly record struct PlistFacts(bool RunAtLoad, bool KeepAliveTruthy, string? Label);

/// <summary>
/// Pure, dependency-free logic for deriving an honest <see cref="ServiceStartType"/> on macOS from
/// launchd metadata (Sym-1 Task 3 brief). No process/file I/O lives here — <see cref="MacOSLaunchdIndex"/>
/// does the shelling-out (`launchctl print-disabled`, `plutil -convert xml1`) and hands this class
/// plain strings/facts. Kept separate so the mapping and parsing rules can be unit-tested with
/// plain fixture strings on every OS, mirroring the AmdGpuTemperature pure/IO split used for the
/// Linux amdgpu hwmon work (Sym-1 Task 2).
/// </summary>
public static class LaunchdStartType
{
    /// <summary>
    /// Maps launchd-derived facts to a <see cref="ServiceStartType"/>. Priority order (binding,
    /// per the brief):
    /// <list type="number">
    /// <item>label present in the `launchctl print-disabled` override table → <see cref="ServiceStartType.Disabled"/>,
    /// regardless of whether a plist was found for it.</item>
    /// <item>no plist found at all (a job submitted at runtime via `launchctl submit`/`bootstrap`,
    /// with no on-disk LaunchAgent/LaunchDaemon) → <see cref="ServiceStartType.Unknown"/> — honest,
    /// not a bug: we genuinely don't know its start policy.</item>
    /// <item>plist found, and either RunAtLoad is true or KeepAlive is a truthy value (bool true or
    /// non-empty dict) → <see cref="ServiceStartType.Automatic"/>.</item>
    /// <item>plist found, neither set → <see cref="ServiceStartType.Manual"/> (present, but not
    /// auto-launched by launchd itself).</item>
    /// </list>
    /// </summary>
    public static ServiceStartType Map(bool plistFound, bool isDisabled, bool runAtLoad, bool keepAliveTruthy)
    {
        if (isDisabled) return ServiceStartType.Disabled;
        if (!plistFound) return ServiceStartType.Unknown;
        return (runAtLoad || keepAliveTruthy) ? ServiceStartType.Automatic : ServiceStartType.Manual;
    }

    /// <summary>
    /// Parses one `launchctl print-disabled &lt;domain&gt;` invocation's output, e.g.:
    /// <code>
    ///     disabled services = {
    ///         "com.apple.something" =&gt; enabled
    ///         "com.apple.other" =&gt; disabled
    ///     }
    /// </code>
    /// Returns only the labels whose value is literally "disabled". `print-disabled` also lists
    /// labels that are explicitly re-*enabled* (overriding a system-default disable) — those must
    /// NOT be treated as disabled, which is why this isn't a simple "does the label appear" check.
    /// Malformed/unparseable lines are skipped rather than thrown (this is best-effort metadata,
    /// not a hard dependency the whole enumeration should fail on).
    /// </summary>
    public static IReadOnlySet<string> ParseDisabledLabels(string printDisabledOutput)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(printDisabledOutput)) return result;

        foreach (var rawLine in printDisabledOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '"') continue;

            var endQuote = line.IndexOf('"', 1);
            if (endQuote < 0) continue;
            var label = line[1..endQuote];
            if (label.Length == 0) continue;

            var arrow = line.IndexOf("=>", endQuote, StringComparison.Ordinal);
            if (arrow < 0) continue;

            var state = line[(arrow + 2)..].Trim();
            if (state.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                result.Add(label);
        }

        return result;
    }

    /// <summary>
    /// Parses a plist already converted to XML text (via `plutil -convert xml1 -o -`, which
    /// normalizes both binary and already-XML plists to the same XML shape) and extracts the
    /// top-level RunAtLoad / KeepAlive / Label keys. Only the top-level &lt;dict&gt; is inspected —
    /// launchd job keys are always top-level, never nested under something else.
    /// </summary>
    /// <returns>False if the input isn't parseable XML or has no top-level dict; RunAtLoad/KeepAlive
    /// default to false and Label to null in that case (caller should treat as "no facts").</returns>
    public static bool TryParsePlist(string plistXml, out PlistFacts facts)
    {
        facts = default;
        if (string.IsNullOrWhiteSpace(plistXml)) return false;

        try
        {
            // DtdProcessing.Ignore + a null XmlResolver: plist files carry a DOCTYPE referencing
            // http://www.apple.com/DTDs/PropertyList-1.0.dtd. We must never attempt to fetch that
            // (no network access assumed/allowed here, and it would be needless I/O either way) —
            // this makes parsing fully offline regardless of whether the DOCTYPE line is present.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver   = null,
            };
            using var stringReader = new StringReader(plistXml);
            using var xmlReader    = XmlReader.Create(stringReader, settings);
            var doc  = XDocument.Load(xmlReader);
            var dict = doc.Root?.Element("dict");
            if (dict is null) return false;

            bool    runAtLoad = false;
            bool    keepAlive = false;
            string? label     = null;

            // A plist <dict> is a flat sequence of <key>K</key><value/> element pairs (no text
            // nodes matter — Elements() already filters those out).
            var children = dict.Elements().ToList();
            for (int i = 0; i < children.Count - 1; i++)
            {
                if (children[i].Name.LocalName != "key") continue;
                var key   = children[i].Value;
                var value = children[i + 1];

                switch (key)
                {
                    case "RunAtLoad":
                        runAtLoad = value.Name.LocalName == "true";
                        break;
                    case "KeepAlive":
                        keepAlive = value.Name.LocalName switch
                        {
                            "true"  => true,
                            "false" => false,
                            // Non-empty dict (e.g. {Crashed: true}, {PathState: {...}}) counts as
                            // truthy; an empty dict is the honest "set but vacuous" case.
                            "dict"  => value.Elements().Any(),
                            _       => false,
                        };
                        break;
                    case "Label":
                        if (value.Name.LocalName == "string")
                            label = value.Value;
                        break;
                }
            }

            facts = new PlistFacts(runAtLoad, keepAlive, label);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
