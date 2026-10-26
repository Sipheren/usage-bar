import Foundation
import UsageBarCore

/// First launch only: finds which sign-ins exist so the fresh settings hold just those accounts.
/// Reads no secret and shows no prompt: the Claude Code Keychain item by its attributes, `~/.claude.json`,
/// and the Claude app's config.json. Never touches Claude Safe Storage or Safari's cookies.
enum SignInDetector {
    static func detect() async -> FoundSignIns {
        let cliItem = await ClaudeCodeCredentials.itemExists()
        let cli = ClaudeCodeAccountInfo.read()
        let config = try? DesktopCredentialReader().readConfig()
        let found = FoundSignIns(claudeCodeLogin: cliItem, claudeCodeAccountUuid: cli?.accountUuid,
                                 claudeAppHasTokenCache: config?.encryptedCaches.contains { $0.0 == "oauth:tokenCacheV2" } ?? false,
                                 claudeAppAccountUuid: config?.lastKnownAccountUuid)
        let adding = found.kinds.isEmpty ? "no accounts" : found.kinds.map(\.rawValue).joined(separator: ", ")
        Log.ui.notice("first run: Claude Code login \(cliItem ? "found" : "not found", privacy: .public); adding \(adding, privacy: .public)")
        return found
    }
}
