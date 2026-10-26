import Foundation

/// What a first launch found signed in on this Mac. The app gathers it without reading any secret: a
/// metadata-only Keychain lookup for Claude Code, and the Claude app's config.json. Safari is never
/// probed (it needs Full Disk Access); users add it from Settings.
public struct FoundSignIns: Equatable, Sendable {
    /// Claude Code's Keychain item exists.
    public var claudeCodeLogin: Bool
    /// `oauthAccount.accountUuid` from `~/.claude.json`.
    public var claudeCodeAccountUuid: String?
    /// The Claude app's config.json holds a non-empty `oauth:tokenCacheV2`.
    public var claudeAppHasTokenCache: Bool
    /// `lastKnownAccountUuid` from the Claude app's config.json.
    public var claudeAppAccountUuid: String?

    public init(claudeCodeLogin: Bool = false, claudeCodeAccountUuid: String? = nil,
                claudeAppHasTokenCache: Bool = false, claudeAppAccountUuid: String? = nil) {
        self.claudeCodeLogin = claudeCodeLogin
        self.claudeCodeAccountUuid = claudeCodeAccountUuid
        self.claudeAppHasTokenCache = claudeAppHasTokenCache
        self.claudeAppAccountUuid = claudeAppAccountUuid
    }

    /// Sources to create accounts for, in order. The Claude app is skipped when it is signed in to the
    /// Claude Code account: the fetcher refuses that pairing, so the account could never show numbers.
    public var kinds: [AccountSource.Kind] {
        var out: [AccountSource.Kind] = []
        if claudeCodeLogin { out.append(.claudeCode) }
        if claudeAppHasTokenCache, let app = claudeAppAccountUuid?.lowercased(), !app.isEmpty,
           app != claudeCodeAccountUuid?.lowercased() {
            out.append(.claudeDesktop)
        }
        return out
    }
}

extension AppSettings {
    /// Settings for a fresh install: one account per sign-in found, none when nothing was found.
    public init(firstRunWith found: FoundSignIns) {
        self.init(accounts: found.kinds.map(Account.defaultAccount(for:)))
    }
}

/// What the dropdown shows for the chosen layout and number of accounts.
public enum DropdownContent: Equatable, Sendable {
    /// No accounts: "No accounts" and a button to Settings > Accounts, whatever the layout.
    case empty
    /// Layout A: every account, one under the other.
    case stacked
    /// Layout B: one account, with the tab strip only when there is another to switch to.
    case oneAtATime(tabs: Bool)

    public init(layout: DropdownLayout, accountCount: Int) {
        if accountCount == 0 {
            self = .empty
        } else if layout == .bothAccounts {
            self = .stacked
        } else {
            self = .oneAtATime(tabs: accountCount > 1)
        }
    }
}
