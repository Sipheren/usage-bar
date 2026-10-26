import AppKit
import UsageBarCore

/// Draws the status item label from a `MenuBarModel`. Always monochrome: a template image for the live
/// menu bar (macOS tints it), or explicit light/dark colours for the PNG snapshots. Never colours by usage.
enum MenuBarRenderer {
    struct Palette {
        var fg: NSColor
        var track: NSColor
    }

    static func palette(dark: Bool, template: Bool) -> Palette {
        if template {
            return Palette(fg: .black, track: NSColor.black.withAlphaComponent(0.3))
        }
        return dark
            ? Palette(fg: NSColor(white: 1, alpha: 0.95), track: NSColor(white: 1, alpha: 0.28))
            : Palette(fg: NSColor(white: 0, alpha: 0.85), track: NSColor(white: 0, alpha: 0.18))
    }

    static let height: CGFloat = 22
    static let valueFont = NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .medium)
    static let tagFont = NSFont.systemFont(ofSize: 9, weight: .bold)

    /// `explicitColours` forces a non-template image with light/dark colours (used for the PNG snapshots).
    static func image(for model: MenuBarModel, dark: Bool, explicitColours: Bool = false) -> NSImage {
        let template = !explicitColours
        let pal = palette(dark: dark, template: template)
        let width = ceil(layout(model, pal, draw: false))
        let img = NSImage(size: NSSize(width: max(width, 1), height: height), flipped: true) { _ in
            _ = layout(model, pal, draw: true)
            return true
        }
        img.isTemplate = template
        img.accessibilityDescription = model.accessibilityLabel
        return img
    }

    /// Lays out (and optionally draws) the label; returns its width.
    private static func layout(_ model: MenuBarModel, _ pal: Palette, draw: Bool) -> CGFloat {
        var x: CGFloat = 0
        x += ClaudeSpark.draw(at: x, barHeight: height, color: pal.fg, draw: draw)
        guard !model.showsSparkOnly else { return x }

        let ctx = draw ? NSGraphicsContext.current?.cgContext : nil
        for account in model.accounts {
            x += 9
            if account.stale, let ctx {
                ctx.saveGState()
                ctx.setAlpha(0.5)
                ctx.beginTransparencyLayer(auxiliaryInfo: nil)
            }
            x += drawTag(account.tag, at: x, color: pal.fg, draw: draw)
            x += 4
            if account.missing {
                x += drawText("–", at: x, font: valueFont, color: pal.fg, draw: draw)
            } else if model.style == .miniBars {
                x += drawBars(account.values, at: x, pal: pal, draw: draw)
            } else {
                for (i, v) in account.values.enumerated() {
                    if i > 0 { x += drawText(model.separator, at: x, font: valueFont, color: pal.fg, draw: draw) }
                    x += drawText(model.valueText(v), at: x, font: valueFont, color: pal.fg, draw: draw)
                }
            }
            if account.stale, let ctx {
                ctx.endTransparencyLayer()
                ctx.restoreGState()
            }
        }
        return x
    }

    private static func attributes(_ font: NSFont, _ color: NSColor) -> [NSAttributedString.Key: Any] {
        [.font: font, .foregroundColor: color]
    }

    private static func drawText(_ s: String, at x: CGFloat, font: NSFont, color: NSColor, draw: Bool) -> CGFloat {
        let str = NSAttributedString(string: s, attributes: attributes(font, color))
        let size = str.size()
        if draw { str.draw(at: NSPoint(x: x, y: (height - size.height) / 2)) }
        return size.width
    }

    /// Tag in an outlined rounded box: 13 pt tall, min 13 wide, 1.2 border, radius 3, 9 pt bold.
    private static func drawTag(_ tag: String, at x: CGFloat, color: NSColor, draw: Bool) -> CGFloat {
        let str = NSAttributedString(string: tag, attributes: attributes(tagFont, color))
        let ts = str.size()
        let w = max(13, ceil(ts.width) + 4)
        if draw {
            let rect = NSRect(x: x + 0.6, y: (height - 13) / 2 + 0.6, width: w - 1.2, height: 13 - 1.2)
            let box = NSBezierPath(roundedRect: rect, xRadius: 2.6, yRadius: 2.6)
            box.lineWidth = 1.2
            color.setStroke()
            box.stroke()
            str.draw(at: NSPoint(x: x + (w - ts.width) / 2, y: (height - ts.height) / 2))
        }
        return w
    }

    /// Two or three stacked 26 x 4 bars, filled in the foreground colour only.
    private static func drawBars(_ values: [MenuBarValue], at x: CGFloat, pal: Palette, draw: Bool) -> CGFloat {
        let w: CGFloat = 26, h: CGFloat = 4, gap: CGFloat = 2
        guard draw else { return w }
        let total = CGFloat(values.count) * h + CGFloat(max(0, values.count - 1)) * gap
        var y = (height - total) / 2
        for v in values {
            let track = NSRect(x: x, y: y, width: w, height: h)
            pal.track.setFill()
            NSBezierPath(roundedRect: track, xRadius: 2, yRadius: 2).fill()
            let frac = min(max(CGFloat(v.percent) / 100, 0), 1)
            if frac > 0 {
                pal.fg.setFill()
                NSBezierPath(roundedRect: NSRect(x: x, y: y, width: max(h, w * frac), height: h), xRadius: 2, yRadius: 2).fill()
            }
            y += h + gap
        }
        return w
    }
}

extension NSColor {
    convenience init(hex: String) {
        let rgb = RGB(hex: hex) ?? RGB(r: 0.5, g: 0.5, b: 0.5)
        self.init(srgbRed: rgb.r, green: rgb.g, blue: rgb.b, alpha: 1)
    }

    var hexString: String {
        guard let c = usingColorSpace(.sRGB) else { return "#808080" }
        return RGB(r: Double(c.redComponent), g: Double(c.greenComponent), b: Double(c.blueComponent)).hex
    }
}

/// The Claude spark, drawn from its vector trace (ClaudeSparkPath) at 16 pt, centred on the bar.
/// Being a vector it is crisp at every scale, and filled with the template or explicit colour.
enum ClaudeSpark {
    static let glyphSize: CGFloat = 16

    /// Draws the spark at `x`, vertically centred in a bar of `barHeight`. Returns its width.
    static func draw(at x: CGFloat, barHeight: CGFloat, color: NSColor, draw: Bool) -> CGFloat {
        guard draw, let ctx = NSGraphicsContext.current?.cgContext else { return glyphSize }
        let rect = CGRect(x: x, y: (barHeight - glyphSize) / 2, width: glyphSize, height: glyphSize)
        ctx.saveGState()
        ctx.addPath(ClaudeSparkPath.path(in: rect))   // the image context is flipped: y down, as the path
        ctx.setFillColor(color.cgColor)
        ctx.fillPath()
        ctx.restoreGState()
        return glyphSize
    }
}
