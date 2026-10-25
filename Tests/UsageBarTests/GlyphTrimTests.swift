import CoreGraphics
import XCTest
@testable import UsageBarCore

final class GlyphTrimTests: XCTestCase {
    /// 10x8 RGBA image, transparent except an opaque block at x 3...6, y 2...4 (top-left origin).
    func makeImage() -> CGImage {
        let w = 10, h = 8
        var px = [UInt8](repeating: 0, count: w * h * 4)
        for y in 2...4 { for x in 3...6 { px[(y * w + x) * 4 + 3] = 255 } }
        let ctx = CGContext(data: &px, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        return ctx.makeImage()!
    }

    func testAlphaBoundsFindsTheOpaqueBlock() {
        XCTAssertEqual(GlyphTrim.alphaBounds(of: makeImage()), CGRect(x: 3, y: 2, width: 4, height: 3))
    }

    func testCroppedKeepsOnlyTheGlyph() throws {
        let cropped = try XCTUnwrap(GlyphTrim.cropped(makeImage()))
        XCTAssertEqual(cropped.width, 4)
        XCTAssertEqual(cropped.height, 3)
    }

    func testFullyTransparentImageHasNoBounds() {
        let w = 4, h = 4
        var px = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &px, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        XCTAssertNil(GlyphTrim.alphaBounds(of: ctx.makeImage()!))
    }
}
