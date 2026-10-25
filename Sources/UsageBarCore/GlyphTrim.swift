import CoreGraphics

/// Trims transparent padding from an icon so it can be drawn at a chosen glyph height.
public enum GlyphTrim {
    /// Bounding box (pixels, top-left origin) of pixels whose alpha exceeds `threshold`; nil if none.
    public static func alphaBounds(of image: CGImage, threshold: UInt8 = 8) -> CGRect? {
        let w = image.width, h = image.height
        guard w > 0, h > 0 else { return nil }
        var px = [UInt8](repeating: 0, count: w * h * 4)
        let drawn: Bool = px.withUnsafeMutableBytes { buf in
            guard let ctx = CGContext(data: buf.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        guard drawn else { return nil }
        // Row 0 of the buffer is the top row of the image.
        var minX = w, minY = h, maxX = -1, maxY = -1
        for y in 0..<h {
            for x in 0..<w where px[(y * w + x) * 4 + 3] > threshold {
                minX = min(minX, x); maxX = max(maxX, x)
                minY = min(minY, y); maxY = max(maxY, y)
            }
        }
        guard maxX >= 0 else { return nil }
        return CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1)
    }

    public static func cropped(_ image: CGImage, threshold: UInt8 = 8) -> CGImage? {
        guard let r = alphaBounds(of: image, threshold: threshold) else { return nil }
        return image.cropping(to: r)
    }
}
