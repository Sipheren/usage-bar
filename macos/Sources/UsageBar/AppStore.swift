import AppKit
import Combine
import ServiceManagement
import UsageBarCore

struct AccountState: Equatable {
    var snapshot: UsageSnapshot?
    var lastSuccess: Date?
    var issue: AccountIssue?
    var email: String?
    /// 429 back-off (Retry-After up to an hour, else 5 min): no fetches of any kind before this time.
    var waitUntil: Date?
    /// Last time a request was made for this account, successful or not.
    var lastAttempt: Date?
    /// The route the last fetch used ("token", "cookie" or "safari"). A Claude app account on "token"
    /// polls no faster than a Claude Code login.
    var lastRoute: String?
    var loading = false
}

enum CostState: Equatable {
    case counting
    case ready(CostSummary)
}

@MainActor
final class AppStore: ObservableObject {
    static let settingsKey = "settings.v1"
    /// A copy of saved settings that couldn't be read at all, made before a first run saves over them.
    static let unreadableSettingsKey = "settings.v1.unreadable"
    static let rateKey = "exchangeRates.v2"
    static let emailsKey = "accountEmails.v1"

    @Published var settings: AppSettings {
        didSet { settingsChanged(from: oldValue) }
    }
    @Published private(set) var states: [UUID: AccountState] = [:]
    @Published private(set) var costs: [UUID: CostState] = [:]
    @Published private(set) var now = Date()
    @Published private(set) var rates: ExchangeRates
    @Published var selectedTab: UUID?
    /// Set by the status controller: closes the menu and opens Settings on a tab.
    var showSettings: ((SettingsTab) -> Void)?

    let fetcher = AccountFetcher()
    private let costService = CostService()
    private var pollTimer: Timer?
    private var tickTimer: Timer?
    private var costTimer: Timer?
    private var inFlight = Set<UUID>()
    /// After a Keychain denial, stop asking until the user acts (Refresh Now or Connect).
    private var keychainBlocked = false
    private var connectRequested = false
    private var costRunning = false
    /// Demo mode: built-in sample data, no network, no credential or log reads, nothing saved.
    let demo: Bool

    /// The saved settings, or nil on a first run (none saved, or unreadable).
    static func savedSettings() -> AppSettings? {
        guard let data = UserDefaults.standard.data(forKey: settingsKey) else { return nil }
        return try? JSONDecoder().decode(AppSettings.self, from: data)
    }

    /// `demoAccounts` (0 to 3) starts demo mode. `firstRun` is what `SignInDetector` found, used only
    /// when no settings are saved yet; saved settings are loaded as they are.
    init(demoAccounts: Int? = nil, firstRun found: FoundSignIns = FoundSignIns()) {
        demo = demoAccounts != nil
        let defaults = UserDefaults.standard
        if let count = demoAccounts {
            settings = AppSettings(accounts: DemoData.accounts(count: count))
            rates = DemoData.rates
            selectedTab = settings.accounts.first?.id   // the tab with the cost chart, for screenshots
            return
        }
        if var s = AppStore.savedSettings() {
            if !s.unreadableAccounts.isEmpty {
                Log.ui.notice("settings: \(s.unreadableAccounts.count, privacy: .public) saved account(s) unreadable by this build; kept and not shown")
            }
            // One-time moves for settings saved before the current defaults (miniBars, Work → Safari).
            let migrated = s.applyMigrations()
            settings = s
            if migrated, let data = try? JSONEncoder().encode(s) { defaults.set(data, forKey: AppStore.settingsKey) }
        } else {
            // Saved settings that exist but can't be read are copied aside before the first run saves over them.
            if let unreadable = defaults.object(forKey: AppStore.settingsKey) {
                if defaults.object(forKey: AppStore.unreadableSettingsKey) == nil {
                    defaults.set(unreadable, forKey: AppStore.unreadableSettingsKey)
                    Log.ui.error("settings: saved settings unreadable; copied to \(AppStore.unreadableSettingsKey, privacy: .public) before starting fresh")
                } else {
                    Log.ui.error("settings: saved settings unreadable; \(AppStore.unreadableSettingsKey, privacy: .public) already holds an earlier copy, left as it is")
                }
            }
            // First run: only the sign-ins found. Persist now so account ids stay stable across launches.
            let fresh = AppSettings(firstRunWith: found)
            settings = fresh
            if let data = try? JSONEncoder().encode(fresh) { defaults.set(data, forKey: AppStore.settingsKey) }
        }
        if let data = defaults.data(forKey: AppStore.rateKey), let r = try? JSONDecoder().decode(ExchangeRates.self, from: data) {
            rates = r
        } else {
            rates = .fallback
        }
        // Emails are display-only metadata, kept so Settings can show them before the first fetch.
        if let emails = defaults.dictionary(forKey: AppStore.emailsKey) as? [String: String] {
            for (id, email) in emails {
                if let uuid = UUID(uuidString: id) { states[uuid, default: AccountState()].email = email }
            }
        }
        loadPersistedStates()
        selectedTab = settings.accounts.last?.id
    }

    // MARK: Lifecycle

    func start() {
        tickTimer = makeTimer(interval: 30) { [weak self] in self?.now = Date() }
        if demo {
            Log.ui.notice("demo mode: sample data, no network or credential reads")
            loadDemoData()
            return
        }
        // A fixed tick: each account fetches only once its own interval is due (FetchPolicy).
        pollTimer = makeTimer(interval: FetchPolicy.tick) { [weak self] in self?.refreshAll(.timer) }
        costTimer = makeTimer(interval: 300) { [weak self] in
            self?.refreshCosts()
            self?.refreshRateIfNeeded()
        }
        refreshAll(.launch)
        refreshCosts()
        refreshRateIfNeeded()
    }

    /// Timers run in `.common` so they keep firing while the menu is open.
    private func makeTimer(interval: TimeInterval, _ block: @escaping @MainActor () -> Void) -> Timer {
        let t = Timer(timeInterval: interval, repeats: true) { _ in
            MainActor.assumeIsolated { block() }
        }
        t.tolerance = min(10, interval / 10)
        RunLoop.main.add(t, forMode: .common)
        return t
    }

    private func settingsChanged(from old: AppSettings) {
        if !demo, let data = try? JSONEncoder().encode(settings) {
            UserDefaults.standard.set(data, forKey: AppStore.settingsKey)
        }
        if old.currency != settings.currency { refreshRateIfNeeded() }
        let oldLogs = old.accounts.map { "\($0.id)\($0.logsDir ?? "")" }
        let newLogs = settings.accounts.map { "\($0.id)\($0.logsDir ?? "")" }
        if oldLogs != newLogs { refreshCosts() }
        if !settings.accounts.contains(where: { $0.id == selectedTab }) { selectedTab = settings.accounts.first?.id }
        // A changed sign-in source: clear the old source's data and re-fetch from the new one.
        for account in settings.accounts {
            if let before = old.accounts.first(where: { $0.id == account.id }), before.source != account.source {
                keychainBlocked = false
                states[account.id] = AccountState()
                Task { await refresh(account, trigger: .manual) }
            }
        }
    }

    /// Opens the Full Disk Access pane so the user can grant access for the Safari source.
    func openFullDiskAccessSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: Usage

    /// The setting, but at least 5 minutes for a sign-in on the OAuth usage endpoint.
    private func backgroundInterval(for account: Account, lastRoute: String?) -> TimeInterval {
        FetchPolicy.backgroundInterval(userMinutes: settings.refreshMinutes,
                                       tokenSource: FetchPolicy.isTokenSource(account.source.kind, lastRoute: lastRoute))
    }

    func refreshAll(_ trigger: FetchTrigger) {
        if trigger == .manual { keychainBlocked = false }
        now = Date()
        for account in settings.accounts {
            Task { await refresh(account, trigger: trigger) }
        }
    }

    /// Menu opened: refresh accounts whose last attempt is 2 minutes old (or their interval, if shorter).
    func menuWillOpen() {
        now = Date()
        refreshAll(.menuOpen)
    }

    /// One-time approval to read Claude Safe Storage (shows the macOS Keychain prompt).
    func connectWork() {
        guard !demo else { return }
        connectRequested = true
        keychainBlocked = false
        for account in settings.accounts where account.source == .claudeDesktop {
            Task { await refresh(account, trigger: .manual) }
        }
    }

    func refresh(_ account: Account, trigger: FetchTrigger) async {
        guard !demo else { return }
        guard !inFlight.contains(account.id) else { return }
        var st = states[account.id] ?? AccountState()
        guard FetchPolicy.shouldFetch(trigger, lastAttempt: st.lastAttempt, waitUntil: st.waitUntil,
                                      backgroundInterval: backgroundInterval(for: account, lastRoute: st.lastRoute),
                                      now: Date()) else { return }
        inFlight.insert(account.id)
        st.loading = true
        st.lastAttempt = Date()
        states[account.id] = st

        let ctx = DesktopContext(allowKeychain: (settings.desktopKeychainApproved || connectRequested) && !keychainBlocked,
                                 cachedChoice: settings.desktopTokenChoice, knownEmail: st.email)
        let report = await fetcher.fetch(account, desktopContext: ctx)

        st = states[account.id] ?? st
        st.loading = false
        if let snap = report.snapshot {
            st.snapshot = snap
            st.lastSuccess = Date()
            st.issue = nil
            st.waitUntil = nil
        } else {
            st.issue = report.issue
        }
        if let route = report.route { st.lastRoute = route }
        if case .rateLimited(let until)? = report.issue {
            st.waitUntil = until
            let header = report.retryAfter.map { "\(Int($0)) s" } ?? "none"
            let wait = max(0, Int(until.timeIntervalSinceNow.rounded()))
            Log.fetch.notice("\(account.tag, privacy: .public): rate limited (429, Retry-After \(header, privacy: .public)), waiting \(wait, privacy: .public) s, retrying at \(Formatters.clockTime(until), privacy: .public)")
        }
        let emailChanged = report.email != nil && report.email != st.email
        if let email = report.email { st.email = email }
        if account.source == .claudeDesktop {
            connectRequested = false
            if report.keychainOK && !settings.desktopKeychainApproved { settings.desktopKeychainApproved = true }
            if report.issue == .keychainDenied { keychainBlocked = true }
            if let choice = report.desktopChoice, settings.desktopTokenChoice[choice.accountUuid] != choice.path {
                settings.desktopTokenChoice[choice.accountUuid] = choice.path
            }
        }
        states[account.id] = st
        if emailChanged { saveEmails() }
        savePersistedStates()
        inFlight.remove(account.id)
        now = Date()
    }

    // MARK: Persisted per-account state (so a relaunch doesn't spend a request)

    private static var stateURL: URL { Paths.appSupport.appendingPathComponent("state.json") }

    private func loadPersistedStates() {
        guard let data = try? Data(contentsOf: AppStore.stateURL),
              let saved = try? JSONDecoder().decode([String: PersistedAccountState].self, from: data) else { return }
        for account in settings.accounts {
            guard let p = saved[account.id.uuidString] else { continue }
            var st = states[account.id] ?? AccountState()
            st.snapshot = p.snapshot
            st.lastSuccess = p.lastSuccess
            st.lastAttempt = p.lastAttempt
            st.waitUntil = p.waitUntil
            if let until = p.waitUntil, until > Date() { st.issue = .rateLimited(until: until) }
            if st.email == nil { st.email = p.email }
            states[account.id] = st
        }
    }

    private func savePersistedStates() {
        guard !demo else { return }
        var out: [String: PersistedAccountState] = [:]
        for account in settings.accounts {
            guard let st = states[account.id] else { continue }
            out[account.id.uuidString] = PersistedAccountState(snapshot: st.snapshot, lastSuccess: st.lastSuccess,
                                                               lastAttempt: st.lastAttempt, waitUntil: st.waitUntil,
                                                               email: st.email)
        }
        do {
            try FileManager.default.createDirectory(at: Paths.appSupport, withIntermediateDirectories: true)
            try JSONEncoder().encode(out).write(to: AppStore.stateURL, options: .atomic)
        } catch {
            Log.fetch.error("could not save account state: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func saveEmails() {
        guard !demo else { return }
        var map: [String: String] = [:]
        for a in settings.accounts { if let e = states[a.id]?.email { map[a.id.uuidString] = e } }
        UserDefaults.standard.set(map, forKey: AppStore.emailsKey)
    }

    var lastUpdated: Date? { states.values.compactMap(\.lastSuccess).max() }

    private func loadDemoData() {
        let now = Date()
        self.now = now
        for account in settings.accounts {
            var st = AccountState()
            st.lastSuccess = now
            switch account.source.kind {
            case .claudeCode:
                st.snapshot = DemoData.personalSnapshot(now: now)
                st.email = DemoData.personalEmail
                costs[account.id] = .ready(DemoData.personalCost(audPerUSD: rates.rates["AUD"] ?? 1.42))
            case .safariSession:
                st.snapshot = DemoData.workSnapshot(now: now)
                st.email = DemoData.workOrganization
            case .claudeDesktop:
                st.snapshot = DemoData.claudeAppSnapshot(now: now)
                st.email = DemoData.claudeAppEmail
            }
            states[account.id] = st
        }
    }

    func state(for account: Account) -> AccountState { states[account.id] ?? AccountState() }

    enum AccountStatus: Equatable {
        case ok(String)
        case problem(AccountIssue)
        case checking
    }

    /// Settings status row: the identity on success, or the exact problem.
    func accountStatus(for account: Account) -> AccountStatus {
        let st = state(for: account)
        let p = presentation(for: account)
        if let note = p.headerNote { return .ok((st.email ?? "Signed in") + " · " + note) }
        if let issue = st.issue { return .problem(issue) }
        if st.snapshot != nil { return .ok(st.email ?? "Signed in") }
        return .checking
    }

    /// How a 429 or other issue should show for this account (see AccountPresentation).
    func presentation(for account: Account) -> AccountPresentation {
        let st = state(for: account)
        return AccountPresentation(issue: st.issue, hasData: st.snapshot != nil)
    }

    func isStale(_ account: Account) -> Bool {
        guard let last = states[account.id]?.lastSuccess else { return false }
        return now.timeIntervalSince(last) > MenuBarModel.staleAfter
    }

    // MARK: Menu bar

    /// Style preview for Settings: the real tags and metric toggles with sample percentages.
    func previewMenuBarModel() -> MenuBarModel {
        let samples: [(Double, Double, Double)] = [(20, 60, 12), (45, 72, 5), (30, 40, 8)]
        let inputs = settings.accounts.filter(\.showInMenuBar).enumerated().map { i, a -> MenuBarInput in
            let v = samples[i % samples.count]
            let snap = UsageSnapshot(limits: [UsageLimit(kind: .session, percent: v.0, resetsAt: nil),
                                              UsageLimit(kind: .weekly, percent: v.1, resetsAt: nil),
                                              UsageLimit(kind: .scoped(id: "sample", name: "Model"), percent: v.2, resetsAt: nil)],
                                     extra: nil, fetchedAt: now)
            return MenuBarInput(tag: a.tag, snapshot: snap, lastSuccess: now)
        }
        return MenuBarModel.build(inputs: inputs, style: settings.menuBarStyle, metrics: settings.metrics, now: now)
    }

    func menuBarModel() -> MenuBarModel {
        let inputs = settings.accounts.filter(\.showInMenuBar).map { a in
            MenuBarInput(tag: a.tag, snapshot: states[a.id]?.snapshot, lastSuccess: states[a.id]?.lastSuccess)
        }
        return MenuBarModel.build(inputs: inputs, style: settings.menuBarStyle, metrics: settings.metrics, now: now)
    }

    // MARK: Cost

    func refreshCosts() {
        guard !demo else { return }
        guard !costRunning else { return }
        let withLogs = settings.accounts.filter { $0.logsDir != nil }
        for a in settings.accounts where a.logsDir == nil { costs[a.id] = nil }
        guard !withLogs.isEmpty else { return }
        for a in withLogs where costs[a.id] == nil { costs[a.id] = .counting }
        costRunning = true
        let roots = Array(Set(withLogs.compactMap { $0.logsDir.map(Paths.expand) }))
        Task {
            let summaries = await costService.refresh(roots: roots)
            for a in settings.accounts {
                guard let dir = a.logsDir, let s = summaries[Paths.expand(dir)] else { continue }
                costs[a.id] = .ready(s)
            }
            costRunning = false
        }
    }

    func cost(for account: Account) -> CostState? { costs[account.id] }

    /// The currency cost figures show in. Falls back to USD while the chosen currency has no rate yet.
    var displayCurrency: String {
        Currency.convert(usd: 1, to: settings.currency, rates: rates) == nil ? "USD" : settings.currency
    }

    /// A US-dollar cost in the display currency: "A$6.20", "€214", "¥938".
    func money(_ usd: Double) -> String {
        let code = displayCurrency
        return Currency.format(Currency.convert(usd: usd, to: code, rates: rates) ?? usd, code: code)
    }

    /// Whole units for chart labels.
    func moneyWhole(_ usd: Double) -> String {
        let code = displayCurrency
        return Currency.formatWhole(Currency.convert(usd: usd, to: code, rates: rates) ?? usd, code: code)
    }

    /// The Exchange rate row in Settings; nil (row hidden) for USD.
    var rateLine: String? { Formatters.rateLine(rates, code: settings.currency) }

    var currencyOptions: [String] {
        Currency.options(localeCode: Locale.current.currency?.identifier, available: Set(rates.rates.keys))
    }

    /// Fetches rates once a day, and straight away when the chosen currency has none. Never for USD.
    func refreshRateIfNeeded() {
        guard !demo, Currency.needsRate(settings.currency) else { return }
        let missing = rates.rates[settings.currency] == nil
        guard missing || rates.needsRefresh(now: Date()) else { return }
        Task {
            do {
                let fresh = try await ExchangeRates.fetch()
                rates = fresh
                if let data = try? JSONEncoder().encode(fresh) { UserDefaults.standard.set(data, forKey: AppStore.rateKey) }
                Log.fetch.info("exchange rates updated: \(fresh.rates.count, privacy: .public) currencies")
            } catch {
                Log.fetch.notice("exchange rate fetch failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    // MARK: Accounts

    func hasAccount(ofKind kind: AccountSource.Kind) -> Bool {
        settings.accounts.contains { $0.source.kind == kind }
    }

    func addAccount(kind: AccountSource.Kind) {
        guard !hasAccount(ofKind: kind) else { return }
        settings.accounts.append(Account.defaultAccount(for: kind))
        if let a = settings.accounts.last { Task { await refresh(a, trigger: .manual) } }
    }

    /// Changes an account's sign-in source from the Settings picker. A source another account uses is refused.
    func setSource(_ kind: AccountSource.Kind, for id: UUID) {
        var s = settings
        guard s.setSource(kind, for: id) else { return }
        settings = s
    }

    func removeAccount(_ id: UUID) {
        settings.accounts.removeAll { $0.id == id }
        states[id] = nil
        costs[id] = nil
    }

    // MARK: Launch at login

    var launchAtLogin: Bool { SMAppService.mainApp.status == .enabled }

    func setLaunchAtLogin(_ on: Bool) -> String? {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            objectWillChange.send()
            return nil
        } catch {
            objectWillChange.send()
            return error.localizedDescription
        }
    }
}
