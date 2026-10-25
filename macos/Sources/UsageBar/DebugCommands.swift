import notify
import Foundation
import ServiceManagement
import UsageBarCore

/// Command-line debug entry points. None of them ever prints a token or log content.
enum DebugCommands {
    static let usage = """
    Usage Bar debug commands:
      --fetch-personal       Fetch Personal usage once (prints HTTP status and limit names/percent only)
      --scan-costs           Full cost scan of ~/.claude/projects without the cache (counts only)
      --dump-desktop-shape   Key names and value types of the Claude app token cache (asks for Keychain access)
      --open-menu [secs]     Ask the running app to open its menu, closing it after secs (default 6)
      --connect-work         Ask the running app to connect the Claude app account (Keychain prompt)
      --demo                 Start the app with built-in sample data (no network, no credentials, nothing saved)
      --unregister-login-item  Remove Usage Bar from Login Items (used by macos/scripts/uninstall.sh)
      --close-menu           Ask the running app to close its menu
      --open-settings TAB    Ask the running app to open Settings on general|accounts|about
      --close-settings       Ask the running app to close Settings
      --set-layout both|one  Ask the running app to switch the dropdown layout
      --render-menubar DIR   This binary renders the menu bar label itself (saved settings + one Personal fetch,
                             Work as not connected) plus a sheet of every style, to PNGs in DIR. Touches no Keychain
                             item except the Claude Code login read through /usr/bin/security.
    """

    /// Returns an exit code if a debug command ran, nil to start the app normally.
    @MainActor
    static func runIfRequested(_ args: [String]) -> Int32? {
        guard args.count > 1 else { return nil }
        switch args[1] {
        case "--fetch-personal": return runBlocking { await fetchPersonal() }
        case "--scan-costs": return scanCosts()
        case "--dump-desktop-shape": return dumpDesktopShape()
        case "--open-menu":
            post(DebugNotification.openMenu, ["seconds": args.count > 2 ? args[2] : "6"])
            return 0
        case "--connect-work":
            post(DebugNotification.connectWork, nil)
            return 0
        case "--close-menu":
            // A Darwin notification reaches the app even while its menu is tracking.
            notify_post(DebugNotification.closeMenuDarwin)
            print("Posted \(DebugNotification.closeMenuDarwin)")
            return 0
        case "--open-settings":
            post(DebugNotification.openSettings, ["tab": args.count > 2 ? args[2] : "general"])
            return 0
        case "--close-settings":
            post(DebugNotification.closeSettings, nil)
            return 0
        case "--set-layout":
            post(DebugNotification.setLayout, ["layout": args.count > 2 ? args[2] : "both"])
            return 0
        case "--render-menubar":
            guard args.count > 2 else { print(usage); return 64 }
            return renderMenuBar(to: URL(fileURLWithPath: args[2]).standardizedFileURL)
        case "--demo":
            return nil   // start the app; AppDelegate reads the flag
        case "--unregister-login-item":
            return unregisterLoginItem()
        case "--help", "-h":
            print(usage)
            return 0
        default:
            // Unknown arguments (e.g. -NSDocumentRevisionsDebugMode from Xcode/open) start the app.
            return args[1].hasPrefix("--") ? { print(usage); return 64 }() : nil
        }
    }

    private static func unregisterLoginItem() -> Int32 {
        let service = SMAppService.mainApp
        guard service.status != .notRegistered else {
            print("Login item: not registered (nothing to do)")
            return 0
        }
        do {
            try service.unregister()
            print("Login item: removed")
            return 0
        } catch {
            print("Login item: could not remove (\(error.localizedDescription))")
            return 1
        }
    }

    private static func post(_ name: Notification.Name, _ info: [String: String]?) {
        DistributedNotificationCenter.default().postNotificationName(name, object: nil, userInfo: info,
                                                                     deliverImmediately: true)
        print("Posted \(name.rawValue)")
    }

    private static func runBlocking(_ work: @escaping @Sendable () async -> Int32) -> Int32 {
        let sem = DispatchSemaphore(value: 0)
        let box = ResultBox()
        Task.detached {
            box.value = await work()
            sem.signal()
        }
        sem.wait()
        return box.value
    }

    private final class ResultBox: @unchecked Sendable { var value: Int32 = 1 }

    // MARK: --fetch-personal

    private static func fetchPersonal() async -> Int32 {
        print("User-Agent: \(ClaudeCLIVersion.userAgent)")
        let report = await AccountFetcher().fetchClaudeCode(configDir: "~/.claude", now: Date())
        print("HTTP status: \(report.httpStatus.map(String.init) ?? "no request made")")
        if let issue = report.issue { print("Issue: \(issue.message)") }
        guard let snap = report.snapshot else { return 1 }
        print("Limits (\(snap.limits.count)):")
        for l in snap.ordered {
            print("  \(l.name): \(Int(l.percent.rounded()))%  (resets_at \(l.resetsAt == nil ? "missing" : "parsed"))")
        }
        print("Extra usage block: \(snap.extra == nil ? "absent" : "present")")
        return 0
    }

    // MARK: --render-menubar

    @MainActor
    private static func renderMenuBar(to dir: URL) -> Int32 {
        var settings = AppSettings()
        if let data = UserDefaults.standard.data(forKey: "settings.v1"),
           var saved = try? JSONDecoder().decode(AppSettings.self, from: data) {
            _ = saved.applyMigrations()   // in-memory only: reflect the defaults a fresh install shows
            settings = saved
        }
        let box = SnapshotBox()
        _ = runBlocking {
            let report = await AccountFetcher().fetchClaudeCode(configDir: "~/.claude", now: Date())
            box.snapshot = report.snapshot
            return 0
        }
        let now = Date()
        let inputs = settings.accounts.filter(\.showInMenuBar).map { a in
            MenuBarInput(tag: a.tag, snapshot: a.source.isClaudeCode ? box.snapshot : nil,
                         lastSuccess: a.source.isClaudeCode && box.snapshot != nil ? now : nil)
        }
        let live = MenuBarModel.build(inputs: inputs, style: settings.menuBarStyle, metrics: settings.metrics, now: now)

        // Sample sheet: every style at normal numbers, plus high percentages (83/96) to prove the
        // label stays monochrome whatever the usage.
        func sample(_ p: (Double, Double), _ w: (Double, Double)) -> [MenuBarInput] {
            func snap(_ v: (Double, Double)) -> UsageSnapshot {
                UsageSnapshot(limits: [UsageLimit(kind: .session, percent: v.0, resetsAt: nil),
                                       UsageLimit(kind: .weekly, percent: v.1, resetsAt: nil)], extra: nil, fetchedAt: now)
            }
            return [MenuBarInput(tag: "P", snapshot: snap(p), lastSuccess: now),
                    MenuBarInput(tag: "W", snapshot: snap(w), lastSuccess: now)]
        }
        let calm = sample((20, 60), (45, 72)), high = sample((20, 60), (83, 96))
        func model(_ i: [MenuBarInput], _ s: MenuBarStyle) -> MenuBarModel {
            MenuBarModel.build(inputs: i, style: s, metrics: MetricSelection(), now: now)
        }
        let samples = [model(calm, .miniBars), model(calm, .withPercent), model(calm, .compact), model(calm, .iconOnly),
                       model(high, .miniBars), model(high, .withPercent), model(high, .compact)]
        do {
            try DebugSnapshot.writeMenuBar([live], prefix: "menubar", to: dir)
            try DebugSnapshot.writeMenuBar(samples, prefix: "menubar-samples", to: dir)
        } catch {
            print("Render failed: \(error.localizedDescription)")
            return 1
        }
        print("Live label: \(live.plainText)")
        print("Wrote menubar-light.png, menubar-dark.png, menubar-samples-light.png, menubar-samples-dark.png to \(dir.path)")
        return 0
    }

    private final class SnapshotBox: @unchecked Sendable { var snapshot: UsageSnapshot? }

    // MARK: --scan-costs

    private static func scanCosts() -> Int32 {
        let root = URL(fileURLWithPath: Paths.expand("~/.claude/projects"), isDirectory: true)
        let scanner = CostScanner(root: root)
        let stats = scanner.scan(now: Date())
        let s = scanner.summary(now: Date())
        let rate = ExchangeRates.fallback.rates["AUD"] ?? 1
        func money(_ usd: Double) -> String { String(format: "US$%.2f (A$%.2f at %.3f)", usd, usd * rate, rate) }
        print("Root: ~/.claude/projects (no cache, nothing written)")
        print("JSONL files found: \(stats.filesFound); modified in last 31 days: \(stats.filesInWindow); read: \(stats.filesRead)")
        print(String(format: "Bytes read: %.2f GB; lines: %d; usage records merged: %d; unique message keys: %d",
                     Double(stats.bytesRead) / 1e9, stats.linesDecoded, stats.recordsMerged, stats.uniqueKeys))
        print("Keys seen in more than one file: \(stats.crossFileDuplicateKeys)")
        print(String(format: "First scan took: %.2f s", stats.seconds))
        print("Today:   \(money(s.todayUSD)), \(s.todayTokens) tokens (\(Formatters.tokens(s.todayTokens)))")
        print("30 days: \(money(s.last30USD)), \(s.last30Tokens) tokens (\(Formatters.tokens(s.last30Tokens)))")
        print("Top model: \(s.topModel ?? "none")")
        print("Per model, 30 days (tokens, US$, how priced):")
        for m in s.perModel {
            let how: String
            switch m.match {
            case .exact(let k): how = k == m.model ? "exact" : "exact via \(k)"
            case .family(let k): how = "family -> \(k)"
            case .unpriced: how = "UNPRICED"
            }
            print(String(format: "  %-28@ %14lld  %10.2f  %@", m.model as NSString, m.tokens, m.usd, how as NSString))
        }
        return 0
    }

    // MARK: --dump-desktop-shape

    private static func dumpDesktopShape() -> Int32 {
        let reader = DesktopCredentialReader()
        let config: DesktopConfig
        do {
            config = try reader.readConfig()
        } catch {
            print("Could not read \(reader.configURL.path): \(error)")
            return 1
        }
        let acct = config.lastKnownAccountUuid
        print("config.json lastKnownAccountUuid: \(acct.map { "<uuid \($0.prefix(8))…>" } ?? "absent")")
        if let a = acct {
            print("Org folders for that account: \(reader.organizationUuids(accountUuid: a).map { "<uuid \($0.prefix(8))…>" })")
        }
        for (name, value) in config.encryptedCaches {
            print("\(name): encrypted string, len \(value.count), v10 prefix: \(value.hasPrefix("djEw"))")
        }
        print("Reading Claude Safe Storage (macOS may ask)…")
        let caches: [(String, Any)]
        do {
            caches = try reader.decryptedCaches(config: config)
        } catch {
            print("Keychain/decrypt failed: \(error)")
            return 2
        }
        for (name, json) in caches {
            print("\n== \(name) (decrypted) : \(DesktopTokenParser.typeDescription(json))")
            print(DesktopTokenParser.shape(of: json, indent: 1))
        }
        let now = Date()
        let cands = (try? reader.candidates(config: config)) ?? []
        print("\nToken candidates: \(cands.count)")
        for c in DesktopTokenParser.ordered(cands, accountUuid: acct, cachedPath: nil, now: now) {
            let exp: String
            if let e = c.expiresAt {
                let mins = Int(e.timeIntervalSince(now) / 60)
                exp = mins >= 0 ? "expires in \(mins) min" : "expired \(-mins) min ago"
            } else {
                exp = "no expiry found"
            }
            print("  path: \(DesktopTokenParser.maskedPath(c.path)) | token len \(c.accessToken.count) | \(exp) | "
                  + "tied to lastKnownAccountUuid: \(c.mentions(acct))")
        }
        do {
            let cookie = try reader.sessionKeyCookie()
            print("\nCookies DB sessionKey for claude.ai: \(cookie == nil ? "not found" : "found (len \(cookie!.count))")")
        } catch {
            print("\nCookies DB sessionKey: error \(error)")
        }
        return 0
    }
}
