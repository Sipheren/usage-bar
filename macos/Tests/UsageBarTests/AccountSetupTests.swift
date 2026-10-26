import XCTest
@testable import UsageBarCore

final class FirstRunAccountsTests: XCTestCase {
    // Synthetic ids from scripts/secret-scan-allow.txt.
    let cliUuid = "00000000-0000-4000-8000-0000000000aa"
    let appUuid = "00000000-0000-4000-8000-0000000000bb"

    func kinds(_ found: FoundSignIns) -> [AccountSource.Kind] {
        AppSettings(firstRunWith: found).accounts.map(\.source.kind)
    }

    func testNothingFoundCreatesNoAccounts() {
        let s = AppSettings(firstRunWith: FoundSignIns())
        XCTAssertEqual(s.accounts, [])
        XCTAssertEqual(s.menuBarStyle, .miniBars)
        XCTAssertEqual(s.dropdownLayout, .bothAccounts)
        XCTAssertEqual(s.currency, "AUD")
        var copy = s
        XCTAssertFalse(copy.applyMigrations())   // a fresh install needs no migration
    }

    func testClaudeCodeOnlyCreatesPersonal() {
        let s = AppSettings(firstRunWith: FoundSignIns(claudeCodeLogin: true, claudeCodeAccountUuid: cliUuid))
        XCTAssertEqual(s.accounts.count, 1)
        let p = s.accounts[0]
        XCTAssertEqual(p.name, "Personal")
        XCTAssertEqual(p.tag, "P")
        XCTAssertEqual(p.source, .claudeCode(configDir: "~/.claude"))
        XCTAssertEqual(p.logsDir, "~/.claude/projects")
        XCTAssertTrue(p.showInMenuBar)
    }

    func testClaudeCodeMetadataWithoutTheKeychainItemIsNotASignIn() {
        // ~/.claude.json can outlive a sign-out; only the Keychain item counts.
        XCTAssertEqual(kinds(FoundSignIns(claudeCodeLogin: false, claudeCodeAccountUuid: cliUuid)), [])
    }

    func testClaudeAppOnlyCreatesClaudeAppAccount() {
        let s = AppSettings(firstRunWith: FoundSignIns(claudeAppHasTokenCache: true, claudeAppAccountUuid: appUuid))
        XCTAssertEqual(s.accounts.count, 1)
        let c = s.accounts[0]
        XCTAssertEqual(c.name, "Claude app")
        XCTAssertEqual(c.tag, "C")
        XCTAssertEqual(c.source, .claudeDesktop)
        XCTAssertNil(c.logsDir)
    }

    func testBothFoundCreatesPersonalThenClaudeApp() {
        let s = AppSettings(firstRunWith: FoundSignIns(claudeCodeLogin: true, claudeCodeAccountUuid: cliUuid,
                                                       claudeAppHasTokenCache: true, claudeAppAccountUuid: appUuid))
        XCTAssertEqual(s.accounts.map(\.tag), ["P", "C"])
        XCTAssertEqual(s.accounts.map(\.source.kind), [.claudeCode, .claudeDesktop])
        XCTAssertNotEqual(s.accounts[0].id, s.accounts[1].id)
    }

    func testClaudeAppOnTheClaudeCodeAccountIsSkipped() {
        let found = FoundSignIns(claudeCodeLogin: true, claudeCodeAccountUuid: cliUuid,
                                 claudeAppHasTokenCache: true, claudeAppAccountUuid: cliUuid)
        XCTAssertEqual(kinds(found), [.claudeCode])
    }

    func testSameAccountMatchIgnoresCase() {
        let found = FoundSignIns(claudeCodeLogin: true, claudeCodeAccountUuid: cliUuid.uppercased(),
                                 claudeAppHasTokenCache: true, claudeAppAccountUuid: cliUuid)
        XCTAssertEqual(kinds(found), [.claudeCode])
    }

    func testClaudeAppOnTheClaudeCodeAccountIsSkippedEvenWithoutTheKeychainItem() {
        // The fetcher refuses this account ("signed in to your Personal account") whatever the Keychain holds,
        // so first run never creates an account that could only ever show that.
        let found = FoundSignIns(claudeCodeLogin: false, claudeCodeAccountUuid: cliUuid,
                                 claudeAppHasTokenCache: true, claudeAppAccountUuid: cliUuid)
        XCTAssertEqual(kinds(found), [])
    }

    func testClaudeAppNeedsBothTokenCacheAndAccount() {
        XCTAssertEqual(kinds(FoundSignIns(claudeAppHasTokenCache: false, claudeAppAccountUuid: appUuid)), [])
        XCTAssertEqual(kinds(FoundSignIns(claudeAppHasTokenCache: true, claudeAppAccountUuid: nil)), [])
        XCTAssertEqual(kinds(FoundSignIns(claudeAppHasTokenCache: true, claudeAppAccountUuid: "")), [])
    }

    func testFirstRunNeverAddsSafari() {
        let all = FoundSignIns(claudeCodeLogin: true, claudeCodeAccountUuid: cliUuid,
                               claudeAppHasTokenCache: true, claudeAppAccountUuid: appUuid)
        XCTAssertFalse(kinds(all).contains(.safariSession))
    }

    func testZeroAccountsSurviveSaveAndLoad() throws {
        let s = AppSettings(firstRunWith: FoundSignIns())
        let loaded = try JSONDecoder().decode(AppSettings.self, from: JSONEncoder().encode(s))
        XCTAssertEqual(loaded.accounts, [])
        XCTAssertEqual(loaded, s)
    }

    func testSavedSettingsWithoutAnAccountListKeepThePersonalAndWorkPair() throws {
        // Settings saved before first-run detection always meant Personal + Work; decoding keeps that.
        let s = try JSONDecoder().decode(AppSettings.self, from: Data(#"{"menuBarStyle":"compact"}"#.utf8))
        XCTAssertEqual(s.accounts.map(\.tag), ["P", "W"])
        XCTAssertEqual(s.accounts.map(\.source.kind), [.claudeCode, .safariSession])
    }

    func testSavedPersonalAndWorkComeBackUnchanged() throws {
        let saved = AppSettings(accounts: Account.personalAndWork())
        let loaded = try JSONDecoder().decode(AppSettings.self, from: JSONEncoder().encode(saved))
        XCTAssertEqual(loaded.accounts, saved.accounts)
    }
}

final class DefaultAccountTests: XCTestCase {
    func testOneDefaultPerSourceWithTheSettingsNamesAndTags() {
        let p = Account.defaultAccount(for: .claudeCode)
        let c = Account.defaultAccount(for: .claudeDesktop)
        let w = Account.defaultAccount(for: .safariSession)
        XCTAssertEqual([p.name, c.name, w.name], ["Personal", "Claude app", "Work"])
        XCTAssertEqual([p.tag, c.tag, w.tag], ["P", "C", "W"])
        XCTAssertEqual([p.source.kind, c.source.kind, w.source.kind], [.claudeCode, .claudeDesktop, .safariSession])
        XCTAssertEqual([p.logsDir, c.logsDir, w.logsDir], ["~/.claude/projects", nil, nil])
    }

    func testEachSourceGetsItsOwnColour() {
        // With all three added, no two accounts share a bar colour.
        let hexes = AccountSource.Kind.allCases.map { Account.defaultAccount(for: $0).colorHex.uppercased() }
        XCTAssertEqual(Set(hexes).count, 3)
    }

    func testPersonalAndWorkIsTheOriginalPair() {
        let pair = Account.personalAndWork()
        XCTAssertEqual(pair.map(\.tag), ["P", "W"])
        XCTAssertEqual(pair.map(\.source), [.claudeCode(configDir: "~/.claude"), .safariSession])
        XCTAssertEqual(pair.map(\.colorHex), ["#D97757", "#3E7CC4"])
    }
}

final class DropdownContentTests: XCTestCase {
    func testNoAccountsShowsTheEmptyStateInEitherLayout() {
        XCTAssertEqual(DropdownContent(layout: .bothAccounts, accountCount: 0), .empty)
        XCTAssertEqual(DropdownContent(layout: .oneAtATime, accountCount: 0), .empty)
    }

    func testAllAccountsLayoutStacksAnyNumber() {
        XCTAssertEqual(DropdownContent(layout: .bothAccounts, accountCount: 1), .stacked)
        XCTAssertEqual(DropdownContent(layout: .bothAccounts, accountCount: 2), .stacked)
        XCTAssertEqual(DropdownContent(layout: .bothAccounts, accountCount: 3), .stacked)
    }

    func testOneAtATimeHidesTheTabsWithASingleAccount() {
        XCTAssertEqual(DropdownContent(layout: .oneAtATime, accountCount: 1), .oneAtATime(tabs: false))
        XCTAssertEqual(DropdownContent(layout: .oneAtATime, accountCount: 2), .oneAtATime(tabs: true))
        XCTAssertEqual(DropdownContent(layout: .oneAtATime, accountCount: 3), .oneAtATime(tabs: true))
    }
}
