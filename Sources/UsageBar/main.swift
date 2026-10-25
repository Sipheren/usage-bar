import AppKit

if let code = MainActor.assumeIsolated({ DebugCommands.runIfRequested(CommandLine.arguments) }) {
    exit(code)
}

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    withExtendedLifetime(delegate) {
        app.run()
    }
}
