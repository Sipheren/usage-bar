import AppKit
import SwiftUI
import UsageBarCore

/// Values baked into Info.plist by build.sh (truthful by construction), with safe fallbacks.
enum AppInfo {
    static func string(_ key: String) -> String? {
        (Bundle.main.object(forInfoDictionaryKey: key) as? String).flatMap { $0.isEmpty ? nil : $0 }
    }
    static var version: String { string("CFBundleShortVersionString") ?? "0.1.0" }
    static var build: String { string("CFBundleVersion") ?? "—" }
    static var commit: String? { string("UBGitCommit") }
    /// "Version 0.1.0 (build 42 · abc1234)", without the hash outside a git checkout.
    static var versionLine: String {
        "Version \(version) (build \(build)" + (commit.map { " · \($0)" } ?? "") + ")"
    }
    static var buildDate: String { string("UBBuildDate") ?? "—" }
    static var signingIdentity: String { string("UBSigningIdentity") ?? "—" }
    static var projectPath: String? { string("UBProjectPath") }
    /// The project folder with the home folder shown as "~".
    static var projectPathDisplay: String {
        projectPath.map { ($0 as NSString).abbreviatingWithTildeInPath } ?? "—"
    }
    static var icon: NSImage { NSApp.applicationIconImage ?? NSImage(size: NSSize(width: 128, height: 128)) }
}

enum SettingsTab: String, CaseIterable {
    case general, accounts, about

    var label: String {
        switch self {
        case .general: return "General"
        case .accounts: return "Accounts"
        case .about: return "About"
        }
    }

    var symbol: String {
        switch self {
        case .general: return "gearshape"
        case .accounts: return "person.2"
        case .about: return "info.circle"
        }
    }
}

// MARK: - Window

/// A standard macOS preferences window: toolbar tabs with SF Symbols, window title = selected tab,
/// fixed width, height fitted to each tab (animated on switch), position remembered.
/// The window is torn down when it closes and rebuilt on the next open: switching tabs leaves
/// observation-tracking registrations inside the SwiftUI hosting graphs that are only freed with them.
@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate {
    static let width: CGFloat = 560
    private static let autosaveName = "UsageBarSettingsWindow"

    private var window: NSWindow?
    private var tabs: SettingsTabViewController?
    private let store: AppStore

    init(store: AppStore) {
        self.store = store
        super.init()
    }

    func windowWillClose(_ notification: Notification) {
        // Release on the next turn, after AppKit has finished closing the window.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.window?.isVisible != true else { return }
            self.window?.delegate = nil
            self.window?.contentViewController = nil
            self.window = nil
            self.tabs = nil
        }
    }

    var isVisible: Bool { window?.isVisible ?? false }

    func show(tab: SettingsTab? = nil) {
        Log.ui.debug("settings opened: \(tab?.rawValue ?? "current", privacy: .public)")
        if window == nil { build() }
        if let tab { tabs?.select(tab) }
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
        window?.orderFrontRegardless()
        tabs?.clearInitialFocus()
    }

    func close() {
        window?.performClose(nil)
    }

    private func build() {
        let tc = SettingsTabViewController()
        tc.tabStyle = .toolbar
        for tab in SettingsTab.allCases {
            let root: AnyView
            switch tab {
            case .general: root = AnyView(GeneralTab().environmentObject(store))
            case .accounts: root = AnyView(AccountsTab().environmentObject(store))
            case .about: root = AnyView(AboutTab().environmentObject(store))
            }
            let host = NSHostingController(rootView: root)
            host.sizingOptions = [.preferredContentSize]
            host.title = tab.label
            let item = NSTabViewItem(viewController: host)
            item.label = tab.label
            item.image = NSImage(systemSymbolName: tab.symbol, accessibilityDescription: tab.label)
            item.identifier = tab.rawValue
            tc.addTabViewItem(item)
        }
        let w = NSWindow(contentViewController: tc)
        w.styleMask = [.titled, .closable, .miniaturizable]
        w.toolbarStyle = .preference
        w.isReleasedWhenClosed = false
        w.initialFirstResponder = nil
        w.autorecalculatesKeyViewLoop = false
        w.animationBehavior = .documentWindow
        w.delegate = self
        tabs = tc
        window = w
        tc.fitWindow(animated: false)
        // Centred the first time; after that the saved position is restored (not in demo mode,
        // which saves nothing).
        if store.demo || !w.setFrameUsingName(SettingsWindowController.autosaveName) { w.center() }
        if !store.demo { w.setFrameAutosaveName(SettingsWindowController.autosaveName) }
        tc.fitWindow(animated: false)
    }
}

final class SettingsTabViewController: NSTabViewController {
    func select(_ tab: SettingsTab) {
        if let i = tabViewItems.firstIndex(where: { ($0.identifier as? String) == tab.rawValue }) {
            selectedTabViewItemIndex = i
        }
    }

    override func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        super.tabView(tabView, didSelect: tabViewItem)
        fitWindow(animated: view.window?.isVisible ?? false)
        clearInitialFocus()
    }

    /// AppKit focuses the first text field (and selects its text) when the window becomes key or a tab
    /// appears. Nothing should be focused until the user clicks a field.
    func clearInitialFocus() {
        DispatchQueue.main.async { [weak self] in
            guard let window = self?.view.window else { return }
            window.makeFirstResponder(nil)
        }
    }

    override func preferredContentSizeDidChange(for viewController: NSViewController) {
        super.preferredContentSizeDidChange(for: viewController)
        if viewController === tabViewItems[selectedTabViewItemIndex].viewController {
            fitWindow(animated: true)
        }
    }

    /// Sizes the window to the selected tab's content: fixed width, height to fit (capped to the screen,
    /// in which case the Accounts form scrolls). Keeps the window's top edge where it is.
    func fitWindow(animated: Bool) {
        guard let window = view.window, selectedTabViewItemIndex >= 0,
              let vc = tabViewItems[selectedTabViewItemIndex].viewController else { return }
        var height = vc.preferredContentSize.height
        if height < 50 { height = vc.view.fittingSize.height }
        let maxHeight = (window.screen ?? NSScreen.main)?.visibleFrame.height ?? 900
        let toolbarAndTitle = window.frame.height - window.contentLayoutRect.height
        height = min(height, maxHeight - toolbarAndTitle - 40)
        let content = NSSize(width: SettingsWindowController.width, height: ceil(height))
        let newFrameSize = window.frameRect(forContentRect: NSRect(origin: .zero, size: content)).size
        var frame = window.frame
        frame.origin.y = frame.maxY - newFrameSize.height
        frame.size = newFrameSize
        window.setFrame(frame, display: true, animate: animated)
    }
}

// MARK: - General

struct GeneralTab: View {
    @EnvironmentObject var store: AppStore
    @State private var launchOn = false
    @State private var launchError: String?

    var body: some View {
        Form {
            Section("Menu bar") {
                Picker("Style", selection: $store.settings.menuBarStyle) {
                    ForEach(MenuBarStyle.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                LabeledContent("Preview") { MenuBarPreviewChip() }
                Toggle("5-hour session", isOn: $store.settings.showSession)
                Toggle("Weekly", isOn: $store.settings.showWeekly)
                Toggle("Model weekly (e.g. Fable)", isOn: $store.settings.showScoped)
            }
            Section("Dropdown") {
                Picker("Layout", selection: $store.settings.dropdownLayout) {
                    ForEach(DropdownLayout.allCases, id: \.self) { Text($0.title).tag($0) }
                }
            }
            Section("Refresh") {
                Picker("Refresh every", selection: $store.settings.refreshMinutes) {
                    ForEach(AppSettings.refreshOptions, id: \.self) { Text("\($0) min").tag($0) }
                }
            }
            Section("General") {
                Toggle("Launch at login", isOn: Binding(
                    get: { launchOn },
                    set: { on in
                        launchError = store.setLaunchAtLogin(on)
                        launchOn = store.launchAtLogin
                    }))
                if let launchError {
                    Text(launchError).font(.callout).foregroundStyle(.red)
                }
                Picker("Currency", selection: $store.settings.currency) {
                    ForEach(store.currencyOptions, id: \.self) { code in
                        Text(currencyName(code)).tag(code)
                    }
                }
                if let line = store.rateLine {
                    LabeledContent("Exchange rate", value: line)
                }
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .frame(width: SettingsWindowController.width)
        .fixedSize(horizontal: false, vertical: true)
        .onAppear { launchOn = store.launchAtLogin }
    }

    /// "Australian Dollar (AUD)".
    private func currencyName(_ code: String) -> String {
        let name = Locale(identifier: "en_US").localizedString(forCurrencyCode: code) ?? code
        return "\(name) (\(code))"
    }
}

/// The menu bar label for the chosen style, on a small menu-bar-like chip that follows light/dark.
/// Uses sample percentages so every style is visible even before any data has arrived.
struct MenuBarPreviewChip: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let dark = scheme == .dark
        Image(nsImage: MenuBarRenderer.image(for: store.previewMenuBarModel(), dark: dark, explicitColours: true))
            .padding(.horizontal, 8)
            .frame(height: 24)
            .background(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(dark ? Color(white: 0.16) : Color(white: 0.92))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.10), lineWidth: 0.5)
            )
            .accessibilityLabel("Menu bar preview")
    }
}

// MARK: - Accounts

struct AccountsTab: View {
    @EnvironmentObject var store: AppStore
    @State private var pendingRemoval: Account?

    var body: some View {
        Form {
            if store.settings.accounts.isEmpty {
                Section {
                    VStack(spacing: 6) {
                        Text("No accounts").font(.headline)
                        Text("No Claude sign-in was found on this Mac. Add an account for each way you sign in: the Claude Code login, the Claude app or a Safari session.")
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                        addAccountMenu.padding(.top, 8)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 10)
                }
            } else {
                ForEach($store.settings.accounts) { $account in
                    AccountSection(account: $account) { pendingRemoval = account }
                }
                Section {
                    HStack {
                        Spacer()
                        addAccountMenu
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: SettingsWindowController.width)
        .fixedSize(horizontal: false, vertical: true)
        .confirmationDialog("Remove \(pendingRemoval?.name ?? "this account")?",
                            isPresented: Binding(get: { pendingRemoval != nil },
                                                 set: { if !$0 { pendingRemoval = nil } }),
                            presenting: pendingRemoval) { account in
            Button("Remove Account", role: .destructive) {
                let id = account.id
                pendingRemoval = nil
                DispatchQueue.main.async { store.removeAccount(id) }
            }
            Button("Cancel", role: .cancel) { pendingRemoval = nil }
        } message: { _ in
            Text("Usage Bar stops showing it. Nothing is signed out and no other app is changed.")
        }
    }
}

extension AccountsTab {
    /// One account per sign-in source: a source already in use is greyed out.
    private var addAccountMenu: some View {
        Menu("Add Account") {
            ForEach(AccountSource.Kind.allCases, id: \.self) { kind in
                Button(kind.label) { store.addAccount(kind: kind) }
                    .disabled(store.hasAccount(ofKind: kind))
            }
        }
        .fixedSize()
        .disabled(AccountSource.Kind.allCases.allSatisfy { store.hasAccount(ofKind: $0) })
    }
}

struct AccountSection: View {
    @EnvironmentObject var store: AppStore
    @Binding var account: Account
    let onRemove: () -> Void

    var body: some View {
        Section {
            LabeledContent("Name") {
                TextField("Name", text: $account.name)
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                    .multilineTextAlignment(.leading)
                    .frame(width: 220)
            }
            LabeledContent("Tag") {
                TextField("Tag", text: Binding(
                    get: { account.tag },
                    set: { account.tag = String($0.trimmingCharacters(in: .whitespaces).prefix(2)) }))
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                    .multilineTextAlignment(.center)
                    .frame(width: 44)
            }
            LabeledContent("Colour") {
                ColorPicker("Colour", selection: Binding(
                    get: { Color(hex: account.colorHex) },
                    set: { account.colorHex = NSColor($0).hexString }), supportsOpacity: false)
                    .labelsHidden()
                    .fixedSize()
            }
            Picker("Sign-in source", selection: Binding(
                get: { account.source.kind },
                set: { store.setSource($0, for: account.id) })) {
                ForEach(AccountSource.Kind.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            LabeledContent("Status") { AccountStatusView(account: account) }
            Toggle("Show in menu bar", isOn: $account.showInMenuBar)
            HStack {
                Spacer()
                Button("Remove Account…", role: .destructive, action: onRemove)
            }
        } header: {
            HStack(spacing: 8) {
                Badge(tag: account.tag, hex: account.badgeHex, size: 18, fontSize: 10, radius: 4)
                Text(account.name.isEmpty ? "Untitled" : account.name)
            }
        }
    }
}

struct AccountStatusView: View {
    @EnvironmentObject var store: AppStore
    let account: Account

    var body: some View {
        let status = store.accountStatus(for: account)
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            switch status {
            case .ok(let who):
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                Text(who).foregroundStyle(.secondary)
            case .problem(let issue):
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                Text(issue.message).foregroundStyle(.secondary)
                    .multilineTextAlignment(.trailing)
                    .fixedSize(horizontal: false, vertical: true)
                if issue.needsFullDiskAccess {
                    Button("Open Privacy Settings…") { store.openFullDiskAccessSettings() }
                        .controlSize(.small)
                } else if issue == .notConnected || issue == .keychainDenied {
                    Button("Connect…") { store.connectWork() }
                        .controlSize(.small)
                }
            case .checking:
                ProgressView().controlSize(.small)
                Text("Checking…").foregroundStyle(.secondary)
            }
        }
    }
}

// MARK: - About

struct AboutTab: View {
    var body: some View {
        Form {
            Section {
                LabeledContent("Built", value: AppInfo.buildDate)
                LabeledContent("Signed by", value: AppInfo.signingIdentity)
                LabeledContent("Reads from") {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Anthropic API — usage and profile")
                        Text("claude.ai — usage via your existing sign-in")
                        Text("Claude Code logs on this Mac — cost")
                        Text("open.er-api.com — exchange rate (none for USD)")
                    }
                    .multilineTextAlignment(.leading)
                    .foregroundStyle(.secondary)
                }
                LabeledContent("Project folder") {
                    HStack(spacing: 8) {
                        Text(AppInfo.projectPathDisplay)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .help(AppInfo.projectPathDisplay)
                        if let path = AppInfo.projectPath {
                            Button("Show in Finder") {
                                NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: path)
                            }
                            .fixedSize()
                        }
                    }
                }
            } header: {
                VStack(spacing: 6) {
                    Image(nsImage: AppInfo.icon)
                        .resizable()
                        .interpolation(.high)
                        .frame(width: 128, height: 128)
                        .accessibilityLabel("Usage Bar icon")
                    Text("Usage Bar")
                        .font(.title2.bold())
                        .foregroundStyle(.primary)
                    Text(AppInfo.versionLine)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                .textCase(nil)
                .frame(maxWidth: .infinity)
                .padding(.top, 4)
                .padding(.bottom, 14)
            } footer: {
                VStack(spacing: 4) {
                    Text("Usage Bar is independent. It reads your existing sign-ins, and all usage data comes from Anthropic.")
                    Text("Built by AI (Claude, by Anthropic) under the direction of its author, Sipheren (sipheren@gmail.com).")
                    Text("MIT License.")
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity)
                .padding(.top, 6)
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .frame(width: SettingsWindowController.width)
        .fixedSize(horizontal: false, vertical: true)
    }
}
