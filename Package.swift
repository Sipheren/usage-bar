// swift-tools-version:6.2
import PackageDescription

let package = Package(
    name: "UsageBar",
    platforms: [.macOS(.v26)],
    targets: [
        // Pure logic: decoding, pace maths, formatting, pricing, log scanning, credential parsing.
        .target(
            name: "UsageBarCore",
            linkerSettings: [.linkedLibrary("sqlite3")]
        ),
        // AppKit / SwiftUI shell: status item, dropdown, settings, debug commands.
        .executableTarget(
            name: "UsageBar",
            dependencies: ["UsageBarCore"]
        ),
        .testTarget(
            name: "UsageBarTests",
            dependencies: ["UsageBarCore"]
        ),
    ],
    swiftLanguageModes: [.v5]
)
