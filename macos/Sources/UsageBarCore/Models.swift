import Foundation

public enum AccountSource: Codable, Equatable, Hashable, Sendable {
    /// Claude Code CLI login. `configDir` may start with `~`.
    case claudeCode(configDir: String)
    /// The account signed in to the Claude desktop app.
    case claudeDesktop
    /// The claude.ai sign-in session from Safari's cookies (read-only).
    case safariSession

    public var isClaudeCode: Bool {
        if case .claudeCode = self { return true }
        return false
    }

    /// Short label for the Settings picker.
    public var pickerLabel: String {
        switch self {
        case .claudeCode: return "Claude Code login"
        case .claudeDesktop: return "Claude app login"
        case .safariSession: return "Safari session"
        }
    }

    /// Picker identity that ignores the config dir, so changing the picker keeps the default dir.
    public enum Kind: String, CaseIterable, Sendable {
        case claudeCode, claudeDesktop, safariSession

        public var label: String {
            switch self {
            case .claudeCode: return "Claude Code login"
            case .claudeDesktop: return "Claude app login"
            case .safariSession: return "Safari session"
            }
        }
    }

    public var kind: Kind {
        switch self {
        case .claudeCode: return .claudeCode
        case .claudeDesktop: return .claudeDesktop
        case .safariSession: return .safariSession
        }
    }

    public static func make(_ kind: Kind) -> AccountSource {
        switch kind {
        case .claudeCode: return .claudeCode(configDir: "~/.claude")
        case .claudeDesktop: return .claudeDesktop
        case .safariSession: return .safariSession
        }
    }

    /// Only the Claude Code login has local cost logs.
    public var defaultLogsDir: String? {
        isClaudeCode ? "~/.claude/projects" : nil
    }
}

public struct Account: Codable, Equatable, Identifiable, Sendable {
    public var id: UUID
    public var name: String
    public var tag: String
    /// Bar colour as `#RRGGBB`.
    public var colorHex: String
    public var showInMenuBar: Bool
    public var source: AccountSource
    /// Claude Code projects folder used for the cost block. nil = no cost block.
    public var logsDir: String?

    public init(id: UUID = UUID(), name: String, tag: String, colorHex: String, showInMenuBar: Bool = true,
                source: AccountSource, logsDir: String?) {
        self.id = id
        self.name = name
        self.tag = tag
        self.colorHex = colorHex
        self.showInMenuBar = showInMenuBar
        self.source = source
        self.logsDir = logsDir
    }

    public static let clayHex = "#D97757"
    public static let blueHex = "#3E7CC4"
    public static let violetHex = "#8E6AC8"

    public static func defaultPersonal() -> Account {
        Account(name: "Personal", tag: "P", colorHex: clayHex, source: .claudeCode(configDir: "~/.claude"),
                logsDir: "~/.claude/projects")
    }

    public static func defaultWork() -> Account {
        Account(name: "Work", tag: "W", colorHex: blueHex, source: .safariSession, logsDir: nil)
    }

    public static func defaultClaudeApp() -> Account {
        Account(name: "Claude app", tag: "C", colorHex: violetHex, source: .claudeDesktop, logsDir: nil)
    }

    /// The account Settings > Add Account and a first run create for a sign-in source (one per source).
    public static func defaultAccount(for kind: AccountSource.Kind) -> Account {
        switch kind {
        case .claudeCode: return defaultPersonal()
        case .claudeDesktop: return defaultClaudeApp()
        case .safariSession: return defaultWork()
        }
    }

    /// The pair every install started with before first-run detection: Personal (Claude Code) and Work (Safari).
    public static func personalAndWork() -> [Account] {
        [defaultPersonal(), defaultWork()]
    }

    /// Badge colour: the mockup's darker pair for the two defaults, otherwise the bar colour darkened.
    public var badgeHex: String {
        switch colorHex.uppercased() {
        case Account.clayHex: return "#B4532F"
        case Account.blueHex: return "#2563A8"
        default:
            guard let rgb = RGB(hex: colorHex) else { return colorHex }
            return RGB(r: rgb.r * 0.8, g: rgb.g * 0.8, b: rgb.b * 0.8).hex
        }
    }
}

public struct RGB: Equatable, Sendable {
    public var r: Double, g: Double, b: Double

    public init(r: Double, g: Double, b: Double) {
        self.r = r; self.g = g; self.b = b
    }

    public init?(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6, let v = UInt32(s, radix: 16) else { return nil }
        r = Double((v >> 16) & 0xFF) / 255
        g = Double((v >> 8) & 0xFF) / 255
        b = Double(v & 0xFF) / 255
    }

    public var hex: String {
        func c(_ x: Double) -> Int { Int((min(max(x, 0), 1) * 255).rounded()) }
        return String(format: "#%02X%02X%02X", c(r), c(g), c(b))
    }
}

public enum MenuBarStyle: String, Codable, CaseIterable, Sendable {
    // Declaration order is the Settings picker order (raw values are what's saved).
    case miniBars, withPercent, compact, iconOnly

    public var title: String {
        switch self {
        case .miniBars: return "Bars"
        case .withPercent: return "Percentages"
        case .compact: return "Compact"
        case .iconOnly: return "Icon only"
        }
    }
}

public enum DropdownLayout: String, Codable, CaseIterable, Sendable {
    // `bothAccounts` is the saved name from when there were always two; it shows every account.
    case bothAccounts, oneAtATime

    public var title: String {
        switch self {
        case .bothAccounts: return "All accounts"
        case .oneAtATime: return "One at a time"
        }
    }
}

public struct MetricSelection: Equatable, Sendable {
    public var session: Bool
    public var weekly: Bool
    public var scoped: Bool

    public init(session: Bool = true, weekly: Bool = true, scoped: Bool = false) {
        self.session = session; self.weekly = weekly; self.scoped = scoped
    }
}

public struct AppSettings: Codable, Equatable, Sendable {
    public var accounts: [Account]
    public var menuBarStyle: MenuBarStyle
    public var showSession: Bool
    public var showWeekly: Bool
    public var showScoped: Bool
    public var dropdownLayout: DropdownLayout
    public var refreshMinutes: Int
    /// Set once the desktop app's Safe Storage key has been read successfully.
    /// Until then Work stays "not connected" so launching never pops a Keychain prompt unasked.
    public var desktopKeychainApproved: Bool
    /// Desktop token-cache candidate path chosen per desktop account uuid (a key path, not a token).
    public var desktopTokenChoice: [String: String]
    /// One-time migration marker (miniBars default + Work → Safari). False in old saved JSON.
    public var didMigrateMiniBars: Bool
    /// ISO code of the currency cost figures are shown in. USD means no conversion and no rate fetch.
    public var currency: String

    public static let refreshOptions = [1, 2, 5, 10]

    /// Every caller names its accounts: a first run uses `init(firstRunWith:)`, demo mode and debug
    /// fallbacks pass their own list.
    public init(accounts: [Account]) {
        self.accounts = accounts
        menuBarStyle = .miniBars
        showSession = true
        showWeekly = true
        showScoped = false
        dropdownLayout = .bothAccounts
        refreshMinutes = 2
        desktopKeychainApproved = false
        desktopTokenChoice = [:]
        didMigrateMiniBars = true
        currency = "AUD"
    }

    public var metrics: MetricSelection {
        MetricSelection(session: showSession, weekly: showWeekly, scoped: showScoped)
    }

    enum CodingKeys: String, CodingKey {
        case accounts, menuBarStyle, showSession, showWeekly, showScoped,
             dropdownLayout, refreshMinutes, desktopKeychainApproved, desktopTokenChoice, didMigrateMiniBars, currency
    }

    // Every field optional on decode so older saved settings never reset everything. Settings saved
    // without a readable account list always meant Personal + Work, so that stays the fallback.
    public init(from decoder: Decoder) throws {
        self.init(accounts: Account.personalAndWork())
        let c = try decoder.container(keyedBy: CodingKeys.self)
        if let v = try? c.decode([Account].self, forKey: .accounts) { accounts = v }
        if let v = try? c.decode(MenuBarStyle.self, forKey: .menuBarStyle) { menuBarStyle = v }
        if let v = try? c.decode(Bool.self, forKey: .showSession) { showSession = v }
        if let v = try? c.decode(Bool.self, forKey: .showWeekly) { showWeekly = v }
        if let v = try? c.decode(Bool.self, forKey: .showScoped) { showScoped = v }
        if let v = try? c.decode(DropdownLayout.self, forKey: .dropdownLayout) { dropdownLayout = v }
        if let v = try? c.decode(Int.self, forKey: .refreshMinutes) { refreshMinutes = v }
        if let v = try? c.decode(Bool.self, forKey: .desktopKeychainApproved) { desktopKeychainApproved = v }
        if let v = try? c.decode([String: String].self, forKey: .desktopTokenChoice) { desktopTokenChoice = v }
        // Absent in old JSON → false → migrations run once.
        didMigrateMiniBars = (try? c.decode(Bool.self, forKey: .didMigrateMiniBars)) ?? false
        if let v = try? c.decode(String.self, forKey: .currency), !v.isEmpty { currency = v.uppercased() }
    }

    /// One-time moves for settings saved before these defaults changed. Returns true if anything changed.
    /// - menu bar style → miniBars (the new default);
    /// - Work (a `.claudeDesktop` account that was never connected) → Safari session.
    public mutating func applyMigrations() -> Bool {
        guard !didMigrateMiniBars else { return false }
        menuBarStyle = .miniBars
        if !desktopKeychainApproved {
            for i in accounts.indices where accounts[i].source == .claudeDesktop {
                accounts[i].source = .safariSession
                accounts[i].logsDir = nil
            }
        }
        didMigrateMiniBars = true
        return true
    }
}

public enum Paths {
    public static func expand(_ path: String) -> String {
        (path as NSString).expandingTildeInPath
    }

    public static var home: String { NSHomeDirectory() }

    public static var appSupport: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: home + "/Library/Application Support")
        return base.appendingPathComponent("UsageBar", isDirectory: true)
    }

    public static var costCache: URL { appSupport.appendingPathComponent("cost-cache.json") }

    public static var claudeDesktopSupport: URL {
        URL(fileURLWithPath: home + "/Library/Application Support/Claude", isDirectory: true)
    }
}
