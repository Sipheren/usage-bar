// Prints "x y w h scale" (points, plus the main display's backing scale) for the first on-screen
// window owned by an app. Used by macos/scripts/qa/capture.sh. Layer 0 = normal window, 101 = open menu.
import AppKit

let args = CommandLine.arguments
let owner = args.count > 1 ? args[1] : "Usage Bar"
let layer = args.count > 2 ? Int(args[2]) ?? 0 : 0
let list = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
    as? [[String: Any]]) ?? []
let scale = Int(NSScreen.main?.backingScaleFactor ?? 2)
for w in list where (w[kCGWindowOwnerName as String] as? String) == owner
    && (w[kCGWindowLayer as String] as? Int) == layer {
    let b = w[kCGWindowBounds as String] as? [String: Double] ?? [:]
    print(Int(b["X"] ?? 0), Int(b["Y"] ?? 0), Int(b["Width"] ?? 0), Int(b["Height"] ?? 0), scale)
    exit(0)
}
exit(1)
