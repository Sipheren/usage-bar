import XCTest
@testable import UsageBarCore

/// Checks a local fixtures folder against its answer sheet. Fixtures are never committed, so this runs only
/// when USAGEBAR_FIXTURES points at one (regenerate the sheet with `UsageBar --fixtures-expected DIR`).
final class FixtureParityTests: XCTestCase {
    func testExpectedJSONMatchesTheFixtures() throws {
        guard let path = ProcessInfo.processInfo.environment["USAGEBAR_FIXTURES"],
              FileManager.default.fileExists(atPath: path + "/expected.json") else {
            throw XCTSkip("USAGEBAR_FIXTURES is not set to a fixtures folder")
        }
        let dir = URL(fileURLWithPath: path)
        let expected = try String(contentsOf: dir.appendingPathComponent("expected.json"), encoding: .utf8)
        let actual = ParitySheet.build(fixtures: dir).serialized()
        if actual != expected {
            let out = FileManager.default.temporaryDirectory.appendingPathComponent("usagebar-expected-actual.json")
            try? actual.write(to: out, atomically: true, encoding: .utf8)
            XCTFail("expected.json drifted from what the core computes; the core's version is at \(out.path)")
        }
    }
}
