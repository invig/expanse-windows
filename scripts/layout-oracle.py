#!/usr/bin/env python3
"""Answers from the Mac app's own layout maths, for src/Layout.Tests to check the C# port against.

Extracts the functions verbatim from Expanse's Sources/Expanse/CanvasController.swift (and two
extensions from Geometry.swift), adds a generator of random layouts (fixed seed), runs it with
`swift`, and writes tests/layout-fixtures.json: inputs and the Swift's results.

    scripts/layout-oracle.py [path to the Expanse checkout, default ../CANVAS]

freeSpot and snap are instance methods there (they read the window list and the camera); they're
turned into static functions taking those as parameters, by substitutions that must each match
exactly once, so a change on the Mac side stops this script instead of testing the wrong thing.
"""
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(ROOT), "CANVAS")
CONTROLLER = open(os.path.join(MAC, "Sources/Expanse/CanvasController.swift")).read()
GEOMETRY = open(os.path.join(MAC, "Sources/Expanse/Geometry.swift")).read()


def block(source, start):
    """The declaration starting with `start`, through its matching closing brace."""
    i = source.index(start)
    assert source.count(start) == 1, f"{start!r} appears {source.count(start)} times"
    depth, j = 0, source.index("{", i)
    while True:
        c = source[j]
        depth += c == "{"
        depth -= c == "}"
        j += 1
        if depth == 0:
            return source[i:j]


def line(source, start):
    i = source.index(start)
    return source[i:source.index("\n", i)]


def substitute(text, pairs):
    for old, new in pairs:
        assert text.count(old) == 1, f"expected one {old!r}"
        text = text.replace(old, new)
    return text


statics = [
    line(CONTROLLER, "enum Direction {"),
    line(CONTROLLER, "static let gap: CGFloat"),
    line(CONTROLLER, "static let clusterReach: CGFloat"),
    line(CONTROLLER, "static let alignReach: CGFloat"),
    block(CONTROLLER, "static func nearest(_ direction: Direction, from: CGRect, in frames: [CGRect]) -> Int? {"),
    block(CONTROLLER, "static func derive(home:"),
    block(CONTROLLER, "static func separate(_ frames: inout [CGRect]"),
    block(CONTROLLER, "struct IndexPair: Hashable {"),
    block(CONTROLLER, "static func overlappingPairs("),
    block(CONTROLLER, "static func clusters("),
    block(CONTROLLER, "static func tidyUp("),
    block(CONTROLLER, "private static func overlapY("),
    block(CONTROLLER, "private static func overlapX("),
    block(CONTROLLER, "static func gravity("),
    block(CONTROLLER, "static func align("),
    block(CONTROLLER, "static func separateGroups("),
    substitute(block(CONTROLLER, "private func freeSpot(for w: ManagedWindow"), [
        ("private func freeSpot(for w: ManagedWindow, near anchor: ManagedWindow?, onScreenOnly: Bool) -> CGPoint? {",
         "static func freeSpot(size: CGSize, anchorFrame: CGRect?, others: [CGRect], target: CGPoint, visible: CGRect, onScreenOnly: Bool) -> CGPoint? {"),
        ("let others = windows.filter { $0 !== w }.map(\\.canvasFrame)\n", ""),
        ("let size = w.canvasFrame.size\n", ""),
        ("let target = anchor?.canvasFrame.center ?? camera.center(in: viewSize)\n", ""),
        ("let visible = camera.toCanvas(CGRect(origin: .zero, size: viewSize))\n", ""),
        ("if let a = anchor?.canvasFrame {", "if let a = anchorFrame {"),
        ("let away: CGFloat = anchor == nil ? 0 : 5000", "let away: CGFloat = anchorFrame == nil ? 0 : 5000"),
    ]),
    substitute(block(CONTROLLER, "func snap(_ f: CGRect, excluding: Set<CGWindowID>)"), [
        ("func snap(_ f: CGRect, excluding: Set<CGWindowID>) -> (CGRect, [(CGPoint, CGPoint)]) {",
         "static func snap(_ f: CGRect, others: [CGRect], scale: CGFloat, mid: CGPoint) -> (CGRect, [(CGPoint, CGPoint)]) {"),
        ("let firm = 10 / camera.scale", "let firm = 10 / scale"),
        ("let others = windows.filter { !excluding.contains($0.id) }.map(\\.canvasFrame)\n", ""),
        ("let mid = camera.center(in: viewSize)\n", ""),
    ]),
]
extensions = block(GEOMETRY, "extension CGRect {") + "\n\n" + block(GEOMETRY, "extension CGPoint {")

GENERATOR = r'''
// MARK: Random layouts (SplitMix64, so runs are repeatable)

var seed: UInt64 = 0x2026_1028
func next() -> UInt64 {
    seed &+= 0x9E37_79B9_7F4A_7C15
    var z = seed
    z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
    z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
    return z ^ (z >> 31)
}
func int(_ lo: Int, _ hi: Int) -> Int { lo + Int(next() % UInt64(hi - lo + 1)) }
func chance(_ p: Double) -> Bool { Double(next() % 1_000_000) / 1_000_000 < p }

func layout() -> [CGRect] {
    let n = int(2, 12)
    var frames: [CGRect] = []
    let style = int(0, 2)
    var x = int(0, 400), y = int(0, 300), rowHeight = 0
    for _ in 0..<n {
        let w = int(300, 1600), h = int(200, 1100)
        switch style {
        case 0:  // scattered over a big canvas
            frames.append(CGRect(x: int(-2000, 5000), y: int(-1500, 3500), width: w, height: h))
        case 1:  // rows, roughly snapped: gaps near 20, edges nearly lined up
            if x > 3000 { x = int(0, 400); y += rowHeight + int(10, 60); rowHeight = 0 }
            frames.append(CGRect(x: x, y: y + int(-40, 40), width: w, height: h))
            x += w + int(5, 80)
            rowHeight = max(rowHeight, h)
        default: // piled up near one spot, overlapping
            frames.append(CGRect(x: int(0, 900), y: int(0, 700), width: w, height: h))
        }
    }
    return frames
}

func r(_ c: CGRect) -> [Double] { [c.minX, c.minY, c.width, c.height] }
func p(_ c: CGPoint) -> [Double] { [c.x, c.y] }

var out: [String: [[String: AnyCodable]]] = [:]

struct AnyCodable: Encodable {
    let value: Any
    init(_ v: Any) { value = v }
    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch value {
        case let v as Double: try c.encode(v)
        case let v as Int: try c.encode(v)
        case let v as Bool: try c.encode(v)
        case let v as String: try c.encode(v)
        case let v as [Double]: try c.encode(v)
        case let v as [[Double]]: try c.encode(v)
        case let v as [Int]: try c.encode(v)
        case let v as [[Int]]: try c.encode(v)
        case is NSNull: try c.encodeNil()
        case let v as AnyCodable: try c.encode(v)
        case let v as [String: AnyCodable]: try c.encode(v)
        case let v as [AnyCodable]: try c.encode(v)
        default: fatalError("can't encode \(value)")
        }
    }
}
func add(_ name: String, _ fields: [String: Any]) {
    out[name, default: []].append(fields.mapValues { AnyCodable($0) })
}
func none<T>(_ v: T?) -> Any { v.map { $0 as Any } ?? NSNull() }
func d(_ v: CGFloat) -> Double { Double(v) }

let directions: [(String, L.Direction)] = [("left", .left), ("right", .right), ("up", .up), ("down", .down)]
for _ in 0..<300 {
    let frames = layout()

    for (name, d) in directions {
        let from = chance(0.8) ? frames[int(0, frames.count - 1)]
            : CGRect(x: int(-1000, 4000), y: int(-1000, 3000), width: 0, height: 0)
        add("nearest", ["direction": name, "from": r(from), "frames": frames.map(r), "want": none(L.nearest(d, from: from, in: frames))])
    }

    let sizes = frames.map { chance(0.5) ? $0.size : CGSize(width: CGFloat(int(Int($0.width / 2), Int($0.width))), height: CGFloat(int(Int($0.height / 2), Int($0.height)))) }
    add("derive", ["home": frames.map(r), "sizes": sizes.map { [d($0.width), d($0.height)] }, "want": L.derive(home: frames, sizes: sizes).map(p)])

    let onlyOverlaps = chance(0.5)
    let keepFrom = layout().prefix(frames.count)
    let keep = onlyOverlaps ? L.overlappingPairs(Array(keepFrom) + Array(frames.dropFirst(keepFrom.count))) : []
    var separated = frames
    L.separate(&separated, gap: L.gap, onlyOverlaps: onlyOverlaps, keep: keep)
    add("separate", ["frames": frames.map(r), "onlyOverlaps": onlyOverlaps, "keep": keep.map { [$0.a, $0.b] }.sorted { ($0[0], $0[1]) < ($1[0], $1[1]) }, "want": separated.map(r)])

    add("overlappingPairs", ["frames": frames.map(r), "want": L.overlappingPairs(frames).map { [$0.a, $0.b] }.sorted { ($0[0], $0[1]) < ($1[0], $1[1]) }])
    add("clusters", ["frames": frames.map(r), "want": L.clusters(frames, reach: L.clusterReach)])

    var tidied = frames; L.tidyUp(&tidied)
    add("tidyUp", ["frames": frames.map(r), "want": tidied.map(r)])
    var pulled = frames; L.gravity(&pulled, gap: L.gap)
    add("gravity", ["frames": frames.map(r), "want": pulled.map(r)])
    var lined = frames; L.align(&lined, reach: L.alignReach, gap: L.gap)
    add("align", ["frames": frames.map(r), "want": lined.map(r)])
    // Arbitrary groups (not clusters, which are apart by definition), so groups collide.
    let groupCount = int(1, min(4, frames.count))
    let groupOf = frames.indices.map { $0 < groupCount ? $0 : int(0, groupCount - 1) }
    let groups = Dictionary(grouping: frames.indices, by: { groupOf[$0] }).values.map { $0.sorted() }.sorted { $0[0] < $1[0] }
    var grouped = frames; L.separateGroups(&grouped, groups: groups, reach: L.clusterReach)
    add("separateGroups", ["frames": frames.map(r), "groups": groups, "want": grouped.map(r)])

    let anchorIndex: Int? = chance(0.75) ? int(0, frames.count - 1) : nil
    let size = CGSize(width: int(300, 1400), height: int(200, 900))
    let anchor = anchorIndex.map { frames[$0] }
    let target = anchor?.center ?? CGPoint(x: int(0, 3000), y: int(0, 2000))
    let screen = chance(0.5) ? CGSize(width: 1512, height: 982) : CGSize(width: 2560, height: 1440)
    let visible = CGRect(x: CGFloat(int(-500, 2500)), y: CGFloat(int(-500, 1500)), width: screen.width, height: screen.height)
    let onScreenOnly = chance(0.3)
    add("freeSpot", ["size": [d(size.width), d(size.height)], "anchor": none(anchor.map(r)), "others": frames.map(r), "target": p(target),
                     "visible": r(visible), "onScreenOnly": onScreenOnly,
                     "want": none(L.freeSpot(size: size, anchorFrame: anchor, others: frames, target: target, visible: visible, onScreenOnly: onScreenOnly).map(p))])

    for _ in 0..<2 {
        let base = frames[int(0, frames.count - 1)]
        let f = CGRect(x: base.minX + CGFloat(int(-1200, 1200)) + CGFloat(int(-12, 12)) / 4, y: base.minY + CGFloat(int(-900, 900)) + CGFloat(int(-12, 12)) / 4,
                       width: CGFloat(int(300, 1400)), height: CGFloat(int(200, 900)))
        let scale: CGFloat = [1, 0.5, 0.25, 0.1][int(0, 3)]
        let mid = chance(0.3) ? CGPoint(x: f.midX + CGFloat(int(-12, 12)), y: f.midY + CGFloat(int(-12, 12))) : CGPoint(x: int(-1000, 4000), y: int(-1000, 3000))
        let (snapped, lines) = L.snap(f, others: frames, scale: scale, mid: mid)
        add("snap", ["frame": r(f), "others": frames.map(r), "scale": d(scale), "mid": p(mid),
                     "want": AnyCodable(["frame": AnyCodable(r(snapped)), "lines": AnyCodable(lines.map { [d($0.0.x), d($0.0.y), d($0.1.x), d($0.1.y)] })])])
    }
}
// A centred spot on a .5 (odd anchor height) wins only if the two spots before it are blocked,
// to the half pixel; random layouts never get there. Swift rounds it half away from zero.
do {
    let anchor = CGRect(x: 0, y: 0, width: 100, height: 101)
    let others = [anchor, CGRect(x: 120, y: -1000, width: 100, height: 2000), CGRect(x: -500, y: 121, width: 1000, height: 100),
                  CGRect(x: -130, y: -100, width: 120, height: 90.25), CGRect(x: -130, y: 110.75, width: 120, height: 50)]
    let size = CGSize(width: 100, height: 100), visible = CGRect(x: -5000, y: -5000, width: 10000, height: 10000)
    add("freeSpot", ["size": [d(size.width), d(size.height)], "anchor": r(anchor), "others": others.map(r), "target": p(anchor.center),
                     "visible": r(visible), "onScreenOnly": false,
                     "want": none(L.freeSpot(size: size, anchorFrame: anchor, others: others, target: anchor.center, visible: visible, onScreenOnly: false).map(p))])
}
let encoder = JSONEncoder()
encoder.outputFormatting = [.sortedKeys]
FileHandle.standardOutput.write(try! encoder.encode(out.mapValues { $0.map { AnyCodable($0) } }))
'''

program = ("import Foundation\nimport CoreGraphics\n\n" + extensions + "\n\nenum L {\n" + "\n\n".join(statics) + "\n}\n" + GENERATOR)
with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, "oracle.swift")
    open(path, "w").write(program)
    result = subprocess.run(["swiftc", "-O", "-o", os.path.join(tmp, "oracle"), path], capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(result.stderr)
    fixtures = subprocess.run([os.path.join(tmp, "oracle")], capture_output=True, check=True).stdout
data = json.loads(fixtures)
target = os.path.join(ROOT, "tests", "layout-fixtures.json")
with open(target, "w") as f:
    json.dump(data, f, separators=(",", ":"))
print(target, {k: len(v) for k, v in data.items()}, os.path.getsize(target), "bytes")
