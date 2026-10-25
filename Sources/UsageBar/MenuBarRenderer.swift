import AppKit
import ImageIO
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
        guard model.style != .iconOnly else { return x }

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

    /// Fallback only, used if the bundled Claude glyph is missing: 8-ray asterisk, 15 pt.
    fileprivate static func drawFallbackSpark(at x: CGFloat, color: NSColor) {
        let s: CGFloat = 15 / 16
        let oy = (height - 15) / 2
        let path = NSBezierPath()
        func line(_ x1: CGFloat, _ y1: CGFloat, _ x2: CGFloat, _ y2: CGFloat) {
            path.move(to: NSPoint(x: x + x1 * s, y: oy + y1 * s))
            path.line(to: NSPoint(x: x + x2 * s, y: oy + y2 * s))
        }
        line(8, 1.6, 8, 14.4)
        line(1.6, 8, 14.4, 8)
        line(3.5, 3.5, 12.5, 12.5)
        line(12.5, 3.5, 3.5, 12.5)
        path.lineWidth = 1.9 * s
        path.lineCapStyle = .round
        color.setStroke()
        path.stroke()
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

/// The real Claude menu bar glyph: Claude.app's TrayIconTemplate (24/48/72 px, black on transparent),
/// bundled as ClaudeSpark.png / @2x / @3x. Transparent padding is trimmed and the glyph drawn
/// `glyphHeight` tall, tinted like a template image.
enum ClaudeSpark {
    static let glyphHeight: CGFloat = 16

    /// Trimmed glyph at the best resolution available, or nil if the resources are missing.
    static let glyph: (image: NSImage, size: NSSize)? = {
        for name in ["ClaudeSpark@3x", "ClaudeSpark@2x", "ClaudeSpark"] {
            guard let url = Bundle.main.url(forResource: name, withExtension: "png"),
                  let src = CGImageSourceCreateWithURL(url as CFURL, nil),
                  let cg = CGImageSourceCreateImageAtIndex(src, 0, nil),
                  let trimmed = GlyphTrim.cropped(cg) else { continue }
            let size = NSSize(width: glyphHeight * CGFloat(trimmed.width) / CGFloat(trimmed.height), height: glyphHeight)
            return (NSImage(cgImage: trimmed, size: size), size)
        }
        Log.ui.error("ClaudeSpark resources missing; drawing the fallback spark")
        return nil
    }()

    /// Draws the glyph vertically centred in a bar of `barHeight`, filled with `color`. Returns its width.
    static func draw(at x: CGFloat, barHeight: CGFloat, color: NSColor, draw: Bool) -> CGFloat {
        guard let g = glyph else {
            if draw { MenuBarRenderer.drawFallbackSpark(at: x, color: color) }
            return 15
        }
        guard draw, let ctx = NSGraphicsContext.current?.cgContext else { return g.size.width }
        let rect = NSRect(x: x, y: (barHeight - g.size.height) / 2, width: g.size.width, height: g.size.height)
        ctx.saveGState()
        ctx.beginTransparencyLayer(in: rect, auxiliaryInfo: nil)
        NSGraphicsContext.current?.imageInterpolation = .high
        g.image.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        // Keep the glyph's alpha, replace its colour: template black, or the explicit bar/warn colour.
        color.setFill()
        rect.fill(using: .sourceIn)
        ctx.endTransparencyLayer()
        ctx.restoreGState()
        return g.size.width
    }
}
