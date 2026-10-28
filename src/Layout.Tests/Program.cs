using System.Text.Json;
using Expanse;

// The layout maths' tests: `scripts/vm.sh test` (or `dotnet run --project src/Layout.Tests`).
//  1. The nearest-window rule, case by case (the same cases as the Hyprland port's).
//  2. Every function against the Mac app's own Swift, on the random layouts in
//     tests/layout-fixtures.json (made by scripts/layout-oracle.py). Results must match exactly.
int failures = 0, passes = 0;

void Expect(string name, bool ok, string detail)
{
    if (ok) { passes++; return; }
    failures++;
    if (failures <= 25) Console.WriteLine($"FAIL {name}: {detail}");
}

// --- 1. Nearest, by hand ---
var from = new RectD(0, 0, 100, 100);
void Nearest(string name, Direction d, RectD[] boxes, int? want)
{
    int? got = Layout.Nearest(d, from, boxes);
    Expect($"nearest: {name}", got == want, $"want {want?.ToString() ?? "none"}, got {got?.ToString() ?? "none"}");
}
Nearest("a window sharing the row beats a nearer one off it", Direction.Right, [new(500, 20, 100, 100), new(200, 90, 100, 100)], 0);
Nearest("off the row, the closest within the cone", Direction.Right, [new(200, 90, 100, 100), new(400, 150, 100, 100)], 0);
Nearest("mostly below never counts as right", Direction.Right, [new(150, 300, 100, 100)], null);
Nearest("but it is down", Direction.Down, [new(150, 300, 100, 100)], 0);
Nearest("in a row, the nearest gap first", Direction.Right, [new(300, 0, 100, 100), new(150, 0, 100, 100)], 1);
Nearest("equal gaps: the better-aligned one", Direction.Down, [new(50, 200, 100, 100), new(0, 200, 100, 100)], 1);
Nearest("left mirrors right", Direction.Left, [new(-300, 0, 100, 100), new(-150, 10, 100, 100), new(200, 0, 100, 100)], 1);
Nearest("up mirrors down", Direction.Up, [new(0, -300, 100, 100), new(20, -150, 100, 100)], 1);
Nearest("nothing that way", Direction.Up, [new(0, 200, 100, 100), new(300, 0, 100, 100)], null);
Nearest("an overlapping window isn't ahead", Direction.Right, [new(-20, 0, 100, 100)], null);
// Swift's .rounded() is half away from zero (C#'s default is half to even); random layouts
// rarely land on a winning .5, so it's pinned here.
foreach (var (v, want) in new[] { (2.5, 3.0), (-2.5, -3.0), (3.5, 4.0), (0.5, 1.0), (2.4, 2.0) })
    Expect($"round {v}", Layout.Round(v) == want, $"want {want}, got {Layout.Round(v)}");
int byHand = passes;

// --- 2. Against the Swift ---
string? dir = AppContext.BaseDirectory;
while (dir != null && !File.Exists(Path.Combine(dir, "tests", "layout-fixtures.json"))) dir = Path.GetDirectoryName(dir);
if (dir == null) { Console.WriteLine("tests/layout-fixtures.json not found"); return 2; }
using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tests", "layout-fixtures.json")));
var root = doc.RootElement;

static RectD R(JsonElement e) => new(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble(), e[3].GetDouble());
static RectD[] Rs(JsonElement e) => e.EnumerateArray().Select(R).ToArray();
static string Show(IEnumerable<RectD> rs) => string.Join(" ", rs.Select(r => $"[{r.X},{r.Y},{r.W},{r.H}]"));
static bool Same(RectD[] a, RectD[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First == p.Second);

var counts = new Dictionary<string, (int pass, int total)>();
void Check(string fn, int index, bool ok, string detail)
{
    var (p, t) = counts.GetValueOrDefault(fn);
    counts[fn] = (p + (ok ? 1 : 0), t + 1);
    Expect($"{fn} #{index}", ok, detail);
}

foreach (var (c, i) in root.GetProperty("nearest").EnumerateArray().Select((c, i) => (c, i)))
{
    var d = Enum.Parse<Direction>(c.GetProperty("direction").GetString()!, ignoreCase: true);
    int? want = c.GetProperty("want").ValueKind == JsonValueKind.Null ? null : c.GetProperty("want").GetInt32();
    int? got = Layout.Nearest(d, R(c.GetProperty("from")), Rs(c.GetProperty("frames")));
    Check("nearest", i, got == want, $"want {want}, got {got}");
}

foreach (var (c, i) in root.GetProperty("derive").EnumerateArray().Select((c, i) => (c, i)))
{
    var sizes = c.GetProperty("sizes").EnumerateArray().Select(s => new SizeD(s[0].GetDouble(), s[1].GetDouble())).ToArray();
    var got = Layout.Derive(Rs(c.GetProperty("home")), sizes);
    var want = c.GetProperty("want").EnumerateArray().Select(p => new Vec(p[0].GetDouble(), p[1].GetDouble())).ToArray();
    Check("derive", i, got.SequenceEqual(want), $"want {string.Join(" ", want)}, got {string.Join(" ", got)}");
}

foreach (var (c, i) in root.GetProperty("separate").EnumerateArray().Select((c, i) => (c, i)))
{
    var frames = Rs(c.GetProperty("frames"));
    var keep = c.GetProperty("keep").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToHashSet();
    Layout.Separate(frames, Layout.Gap, c.GetProperty("onlyOverlaps").GetBoolean(), keep);
    var want = Rs(c.GetProperty("want"));
    Check("separate", i, Same(frames, want), $"want {Show(want)}, got {Show(frames)}");
}

foreach (var (c, i) in root.GetProperty("overlappingPairs").EnumerateArray().Select((c, i) => (c, i)))
{
    var got = Layout.OverlappingPairs(Rs(c.GetProperty("frames"))).Order().ToList();
    var want = c.GetProperty("want").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToList();
    Check("overlappingPairs", i, got.SequenceEqual(want), $"want {string.Join(" ", want)}, got {string.Join(" ", got)}");
}

foreach (var (c, i) in root.GetProperty("clusters").EnumerateArray().Select((c, i) => (c, i)))
{
    var got = Layout.Clusters(Rs(c.GetProperty("frames")), Layout.ClusterReach);
    var want = c.GetProperty("want").EnumerateArray().Select(v => v.GetInt32()).ToArray();
    Check("clusters", i, got.SequenceEqual(want), $"want {string.Join(",", want)}, got {string.Join(",", got)}");
}

void InPlace(string fn, Action<RectD[], JsonElement> run)
{
    foreach (var (c, i) in root.GetProperty(fn).EnumerateArray().Select((c, i) => (c, i)))
    {
        var frames = Rs(c.GetProperty("frames"));
        run(frames, c);
        var want = Rs(c.GetProperty("want"));
        Check(fn, i, Same(frames, want), $"want {Show(want)}, got {Show(frames)}");
    }
}
InPlace("tidyUp", (f, _) => Layout.TidyUp(f));
InPlace("gravity", (f, _) => Layout.Gravity(f, Layout.Gap));
InPlace("align", (f, _) => Layout.Align(f, Layout.AlignReach, Layout.Gap));
InPlace("separateGroups", (f, c) => Layout.SeparateGroups(f,
    c.GetProperty("groups").EnumerateArray().Select(g => g.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToList(),
    Layout.ClusterReach));

foreach (var (c, i) in root.GetProperty("freeSpot").EnumerateArray().Select((c, i) => (c, i)))
{
    var size = new SizeD(c.GetProperty("size")[0].GetDouble(), c.GetProperty("size")[1].GetDouble());
    RectD? anchor = c.GetProperty("anchor").ValueKind == JsonValueKind.Null ? null : R(c.GetProperty("anchor"));
    var t = c.GetProperty("target");
    var got = Layout.FreeSpot(size, anchor, Rs(c.GetProperty("others")), new Vec(t[0].GetDouble(), t[1].GetDouble()),
        R(c.GetProperty("visible")), c.GetProperty("onScreenOnly").GetBoolean());
    var w = c.GetProperty("want");
    Vec? want = w.ValueKind == JsonValueKind.Null ? null : new Vec(w[0].GetDouble(), w[1].GetDouble());
    Check("freeSpot", i, got == want, $"want {want}, got {got}");
}

foreach (var (c, i) in root.GetProperty("snap").EnumerateArray().Select((c, i) => (c, i)))
{
    var m = c.GetProperty("mid");
    var (frame, lines) = Layout.Snap(R(c.GetProperty("frame")), Rs(c.GetProperty("others")), c.GetProperty("scale").GetDouble(),
        new Vec(m[0].GetDouble(), m[1].GetDouble()));
    var want = c.GetProperty("want");
    var wantLines = want.GetProperty("lines").EnumerateArray()
        .Select(l => new Layout.GuideLine(new Vec(l[0].GetDouble(), l[1].GetDouble()), new Vec(l[2].GetDouble(), l[3].GetDouble()))).ToList();
    Check("snap", i, frame == R(want.GetProperty("frame")) && lines.SequenceEqual(wantLines),
        $"want {R(want.GetProperty("frame"))} {string.Join(" ", wantLines)}, got {frame} {string.Join(" ", lines)}");
}

Console.WriteLine($"by hand: {byHand} of 15 (nearest, rounding)");
foreach (var (fn, (p, t)) in counts) Console.WriteLine($"{fn,-17} {p,5} of {t,5} match the Swift");
Console.WriteLine(failures == 0 ? $"all {passes} pass" : $"{failures} FAILED, {passes} pass");
return failures == 0 ? 0 : 1;
