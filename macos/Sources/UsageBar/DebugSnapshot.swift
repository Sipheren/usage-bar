import AppKit
import SwiftUI
import UsageBarCore

/// Renders menu bar label sample sheets to PNG (used by `--render-menubar`).
@MainActor
enum DebugSnapshot {
    /// Renders the menu bar label for each model on a light and a dark bar, stacked, one PNG per appearance.
    static func writeMenuBar(_ models: [MenuBarModel], prefix: String, to dir: URL) throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for dark in [false, true] {
            let rows = models.map { menuBar(MenuBarRenderer.image(for: $0, dark: dark, explicitColours: true), dark: dark) }
            let width = rows.map(\.size.width).max() ?? 1
            let height = rows.reduce(0) { $0 + $1.size.height }
            let sheet = NSImage(size: NSSize(width: width, height: max(height, 1)), flipped: true) { rect in
                NSColor(hex: dark ? "#1C1C1E" : "#E6E6E9").setFill()
                rect.fill()
                var y: CGFloat = 0
                for r in rows {
                    r.draw(in: NSRect(x: 0, y: y, width: r.size.width, height: r.size.height), from: .zero,
                           operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
                    y += r.size.height
                }
                return true
            }
            try png(sheet, to: dir.appendingPathComponent("\(prefix)-\(dark ? "dark" : "light").png"))
        }
    }

    private static func menuBar(_ img: NSImage, dark: Bool) -> NSImage {
        let pad: CGFloat = 12
        let size = NSSize(width: img.size.width + pad * 2, height: 30)
        return NSImage(size: size, flipped: false) { rect in
            NSColor(hex: dark ? "#1C1C1E" : "#E6E6E9").setFill()
            rect.fill()
            img.draw(in: NSRect(x: pad, y: (size.height - img.size.height) / 2, width: img.size.width, height: img.size.height))
            return true
        }
    }

    private static func png(_ img: NSImage, to url: URL) throws {
        let w = Int(img.size.width * 2), h = Int(img.size.height * 2)
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0) else { throw CocoaError(.fileWriteUnknown) }
        rep.size = img.size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        img.draw(in: NSRect(origin: .zero, size: img.size))
        NSGraphicsContext.restoreGraphicsState()
        try write(rep, to: url)
    }

    private static func write(_ rep: NSBitmapImageRep, to url: URL) throws {
        guard let data = rep.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
        try data.write(to: url, options: .atomic)
    }
}
