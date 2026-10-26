import AppKit
import Combine
import notify
import SwiftUI
import UsageBarCore

enum DebugNotification {
    static let openMenu = Notification.Name("com.sipheren.usagebar.openMenu")
    static let connectWork = Notification.Name("com.sipheren.usagebar.connectWork")
    static let closeMenu = Notification.Name("com.sipheren.usagebar.closeMenu")
    static let openSettings = Notification.Name("com.sipheren.usagebar.openSettings")
    static let closeSettings = Notification.Name("com.sipheren.usagebar.closeSettings")
    static let setLayout = Notification.Name("com.sipheren.usagebar.setLayout")
    static let setStyle = Notification.Name("com.sipheren.usagebar.setStyle")
    static let all = [openMenu, connectWork, closeMenu, openSettings, closeSettings, setLayout, setStyle]
    /// Darwin notification name for closing an open menu (see StatusController).
    static let closeMenuDarwin = "com.sipheren.usagebar.closeMenu"
}

@MainActor
final class StatusController: NSObject, NSMenuDelegate {
    private let store: AppStore
    private let settingsWindow: SettingsWindowController
    private let statusItem: NSStatusItem
    private let menu = NSMenu()
    private var hosting: NSHostingView<AnyView>!
    private var cancellables = Set<AnyCancellable>()
    private var lastModel: MenuBarModel?
    private var updateScheduled = false
    private var closeMenuToken: Int32 = 0
    private var closeMenuPoll: Timer?

    init(store: AppStore) {
        self.store = store
        settingsWindow = SettingsWindowController(store: store)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        buildMenu()
        statusItem.menu = menu
        statusItem.button?.imagePosition = .imageOnly

        store.objectWillChange
            .sink { [weak self] _ in self?.scheduleUpdate() }
            .store(in: &cancellables)
        store.showSettings = { [weak self] tab in self?.openSettingsFromMenu(tab) }

        let dnc = DistributedNotificationCenter.default()
        for name in DebugNotification.all {
            dnc.addObserver(self, selector: #selector(debugNotification(_:)), name: name, object: nil,
                            suspensionBehavior: .deliverImmediately)
        }
        // While the menu is open the main run loop only services `.common`-mode timers: distributed
        // notifications and the main queue wait until it closes. So `--close-menu` posts a Darwin
        // notification that a `.common` timer polls, only while the menu is open (see menuWillOpen).
        notify_register_check(DebugNotification.closeMenuDarwin, &closeMenuToken)
        updateStatusImage(force: true)
    }

    private func buildMenu() {
        menu.delegate = self
        menu.autoenablesItems = false
        let view = NSHostingView(rootView: AnyView(DropdownRoot().environmentObject(store)))
        view.sizingOptions = [.intrinsicContentSize]
        hosting = view
        let item = NSMenuItem()
        item.view = view
        menu.addItem(item)
        resizeHostingView()
        menu.addItem(.separator())

        // Every native item gets an SF Symbol, so the column of icons is consistent.
        func add(_ title: String, _ action: Selector, _ key: String, _ symbol: String) {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
            item.target = self
            item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
            // macOS 27 hides symbol images in menus unless asked; keep the icon column consistent.
            if #available(macOS 27.0, *) { item.preferredImageVisibility = .visible }
            menu.addItem(item)
        }
        add("Refresh Now", #selector(refreshNow), "r", "arrow.clockwise")
        add("About Usage Bar", #selector(openAbout), "", "info.circle")
        add("Settings…", #selector(openSettings), ",", "gearshape")
        menu.addItem(.separator())
        add("Quit Usage Bar", #selector(quit), "q", "power")
    }

    private func resizeHostingView() {
        let size = hosting.fittingSize
        let target = NSSize(width: 360, height: ceil(size.height))
        if hosting.frame.size != target { hosting.frame = NSRect(origin: .zero, size: target) }
    }

    /// objectWillChange fires before the value changes; update on the next turn of the run loop.
    private func scheduleUpdate() {
        guard !updateScheduled else { return }
        updateScheduled = true
        RunLoop.main.perform(inModes: [.common]) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.updateScheduled = false
                self.updateStatusImage(force: false)
                self.resizeHostingView()
            }
        }
    }

    /// Sets a new label image only when the label's content changed. The image is a template, so macOS
    /// tints it for light and dark menu bars itself. Don't observe `effectiveAppearance` to redraw: each
    /// new image makes AppKit rebuild the status item, which fires that observation again (a redraw loop
    /// that held a CPU core busy).
    private func updateStatusImage(force: Bool) {
        guard let button = statusItem.button else { return }
        let model = store.menuBarModel()
        guard force || model != lastModel else { return }
        lastModel = model
        button.image = MenuBarRenderer.image(for: model, dark: false)
        button.setAccessibilityLabel(model.accessibilityLabel)
        button.toolTip = model.showsSparkOnly ? "Claude usage" : model.plainText
    }

    // MARK: NSMenuDelegate

    func menuWillOpen(_ menu: NSMenu) {
        Log.ui.debug("menu opened")
        store.menuWillOpen()
        resizeHostingView()
        var changed: Int32 = 0
        notify_check(closeMenuToken, &changed)   // clear any close request posted before this open
        let poll = Timer(timeInterval: 0.2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                var posted: Int32 = 0
                notify_check(self.closeMenuToken, &posted)
                if posted != 0 { self.menu.cancelTracking() }
            }
        }
        RunLoop.main.add(poll, forMode: .common)
        closeMenuPoll = poll
    }

    func menuDidClose(_ menu: NSMenu) {
        closeMenuPoll?.invalidate()
        closeMenuPoll = nil
    }

    // MARK: Actions

    /// From a button inside the dropdown: the window can only come forward once the menu stops tracking.
    private func openSettingsFromMenu(_ tab: SettingsTab) {
        menu.cancelTracking()
        DispatchQueue.main.async { [weak self] in self?.settingsWindow.show(tab: tab) }
    }

    @objc private func refreshNow() { store.refreshAll(.manual) }
    @objc private func openSettings() { settingsWindow.show(tab: .general) }
    @objc private func openAbout() { settingsWindow.show(tab: .about) }
    @objc private func quit() { NSApp.terminate(nil) }

    @objc private func debugNotification(_ note: Notification) {
        handleDebug(note.name, note.userInfo)
    }

    /// Handles the debug hooks (`UsageBar --open-menu`, `--open-settings`, ...) on the main thread.
    private func handleDebug(_ name: Notification.Name, _ info: [AnyHashable: Any]?) {
        switch name {
        case DebugNotification.openMenu:
            // Opens the menu without touching the mouse and closes it after N seconds at most.
            let seconds = Double(info?["seconds"] as? String ?? "") ?? 6
            let closer = Timer(timeInterval: max(1, min(seconds, 30)), repeats: false) { [weak self] _ in
                MainActor.assumeIsolated { self?.menu.cancelTracking() }
            }
            RunLoop.main.add(closer, forMode: .common)
            statusItem.button?.performClick(nil)
        case DebugNotification.closeMenu:
            menu.cancelTracking()
        case DebugNotification.connectWork:
            store.connectWork()
        case DebugNotification.openSettings:
            let tab = (info?["tab"] as? String).flatMap(SettingsTab.init(rawValue:)) ?? .general
            settingsWindow.show(tab: tab)
        case DebugNotification.closeSettings:
            settingsWindow.close()
        case DebugNotification.setLayout:
            store.settings.dropdownLayout = (info?["layout"] as? String) == "one" ? .oneAtATime : .bothAccounts
        case DebugNotification.setStyle:
            if let style = (info?["style"] as? String).flatMap(MenuBarStyle.init(rawValue:)) {
                store.settings.menuBarStyle = style
            }
        default:
            break
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var store: AppStore?
    private var status: StatusController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let demoAccounts = DemoData.requestedAccountCount(arguments: CommandLine.arguments,
                                                          environment: ProcessInfo.processInfo.environment)
        guard demoAccounts == nil, AppStore.savedSettings() == nil else {
            launch(AppStore(demoAccounts: demoAccounts))
            return
        }
        // First run: look for existing sign-ins (no prompt, no secret read) before creating the accounts.
        Task { @MainActor in
            let found = await SignInDetector.detect()
            self.launch(AppStore(firstRun: found))
        }
    }

    private func launch(_ store: AppStore) {
        self.store = store
        status = StatusController(store: store)
        store.start()
    }
}
