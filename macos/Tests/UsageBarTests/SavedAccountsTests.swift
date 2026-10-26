import XCTest
@testable import UsageBarCore

final class UnreadableAccountTests: XCTestCase {
    // Synthetic ids from scripts/secret-scan-allow.txt.
    let goodId = UUID(uuidString: "00000000-0000-4000-8000-0000000000aa")!

    let good = """
    {"id":"00000000-0000-4000-8000-0000000000aa","name":"Personal","tag":"P","colorHex":"#D97757",
     "showInMenuBar":true,"source":{"claudeCode":{"configDir":"~/.claude"}},"logsDir":"~/.claude/projects"}
    """
    /// A source kind this build doesn't know (as a newer build might write), with every JSON value type.
    let unknownKind = """
    {"id":"00000000-0000-4000-8000-0000000000bb","name":"Browser","tag":"B","colorHex":"#3E7CC4",
     "showInMenuBar":false,"source":{"chromeSession":{"profile":"Default"}},"extra":[1,2.5,null,"x",{"n":-3}]}
    """
    let badUUID = """
    {"id":"not-a-uuid","name":"Work","tag":"W","colorHex":"#3E7CC4","showInMenuBar":true,"source":{"safariSession":{}}}
    """

    func decode(_ json: String) throws -> AppSettings {
        try JSONDecoder().decode(AppSettings.self, from: Data(json.utf8))
    }

    func settings(_ accounts: String...) -> String {
        #"{"menuBarStyle":"compact","accounts":["# + accounts.joined(separator: ",") + "]}"
    }

    /// The saved `accounts` array, parsed so JSON compares by meaning rather than key order.
    func savedAccounts(_ s: AppSettings) throws -> [NSDictionary] {
        let root = try JSONSerialization.jsonObject(with: JSONEncoder().encode(s)) as? NSDictionary
        return try XCTUnwrap(root?["accounts"] as? [NSDictionary])
    }

    func parsed(_ json: String) throws -> NSDictionary {
        try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? NSDictionary)
    }

    func testAnUnknownSourceKindKeepsTheReadableAccountWithItsId() throws {
        let s = try decode(settings(good, unknownKind))
        XCTAssertEqual(s.accounts.map(\.id), [goodId])
        XCTAssertEqual(s.accounts.map(\.tag), ["P"])
        XCTAssertEqual(s.unreadableAccounts.count, 1)
        XCTAssertEqual(s.menuBarStyle, .compact)   // the rest of the settings still load
    }

    func testAnUnknownSourceKindIsWrittenBackUnchanged() throws {
        let s = try decode(settings(good, unknownKind))
        let saved = try savedAccounts(s)
        XCTAssertEqual(saved.count, 2)
        XCTAssertEqual(saved.first?["id"] as? String, goodId.uuidString)
        XCTAssertEqual(saved.last, try parsed(unknownKind))
        // And again on the next load and save.
        let again = try JSONDecoder().decode(AppSettings.self, from: JSONEncoder().encode(s))
        XCTAssertEqual(again, s)
        XCTAssertEqual(try savedAccounts(again).last, try parsed(unknownKind))
    }

    func testABadUUIDKeepsTheReadableAccountAndIsWrittenBackUnchanged() throws {
        let s = try decode(settings(good, badUUID))
        XCTAssertEqual(s.accounts.map(\.id), [goodId])
        XCTAssertEqual(s.unreadableAccounts.count, 1)
        let saved = try savedAccounts(s)
        XCTAssertEqual(saved.count, 2)
        XCTAssertEqual(saved.last, try parsed(badUUID))
    }

    func testOnlyUnreadableAccountsMeansNoneShownNotThePersonalAndWorkPair() throws {
        let s = try decode(settings(unknownKind, badUUID))
        XCTAssertEqual(s.accounts, [])
        XCTAssertEqual(s.unreadableAccounts.count, 2)
        XCTAssertEqual(try savedAccounts(s), [try parsed(unknownKind), try parsed(badUUID)])
    }

    func testAccountsThatAreNotAListStillMeanPersonalAndWork() throws {
        // Settings saved before the account list existed always meant Personal + Work.
        for json in [#"{"menuBarStyle":"compact"}"#,
                     #"{"menuBarStyle":"compact","accounts":null}"#,
                     #"{"menuBarStyle":"compact","accounts":{"id":"x"}}"#] {
            let s = try decode(json)
            XCTAssertEqual(s.accounts.map(\.tag), ["P", "W"], json)
            XCTAssertEqual(s.accounts.map(\.source.kind), [.claudeCode, .safariSession], json)
            XCTAssertEqual(s.unreadableAccounts.count, 0, json)
        }
    }

    func testReadableAccountsRoundTripWithNothingExtra() throws {
        let saved = AppSettings(accounts: Account.personalAndWork())
        let loaded = try JSONDecoder().decode(AppSettings.self, from: JSONEncoder().encode(saved))
        XCTAssertEqual(loaded, saved)
        XCTAssertEqual(loaded.unreadableAccounts.count, 0)
        XCTAssertEqual(try savedAccounts(loaded).count, 2)
    }
}

final class OneAccountPerSourceTests: XCTestCase {
    /// Personal on Claude Code plus a Claude app account.
    func pair() -> AppSettings {
        AppSettings(accounts: [Account.defaultPersonal(), Account.defaultClaudeApp()])
    }

    func testASourceAnotherAccountUsesIsNotAvailable() {
        let s = pair()
        let personal = s.accounts[0].id, app = s.accounts[1].id
        XCTAssertFalse(s.canUseSource(.claudeDesktop, for: personal))
        XCTAssertFalse(s.canUseSource(.claudeCode, for: app))
    }

    func testAnAccountsOwnSourceAndFreeSourcesStayAvailable() {
        let s = pair()
        let personal = s.accounts[0].id
        XCTAssertTrue(s.canUseSource(.claudeCode, for: personal))
        XCTAssertTrue(s.canUseSource(.safariSession, for: personal))
    }

    func testSetSourceRefusesASourceAnotherAccountUses() {
        var s = pair()
        let before = s
        XCTAssertFalse(s.setSource(.claudeDesktop, for: s.accounts[0].id))
        XCTAssertEqual(s, before)
        XCTAssertEqual(s.accounts.map(\.source.kind), [.claudeCode, .claudeDesktop])
    }

    func testSetSourceMovesToAFreeSourceWithItsDefaultLogsFolder() {
        var s = pair()
        let id = s.accounts[0].id
        XCTAssertTrue(s.setSource(.safariSession, for: id))
        XCTAssertEqual(s.accounts[0].source, .safariSession)
        XCTAssertNil(s.accounts[0].logsDir)
        XCTAssertTrue(s.setSource(.claudeCode, for: id))
        XCTAssertEqual(s.accounts[0].source, .claudeCode(configDir: "~/.claude"))
        XCTAssertEqual(s.accounts[0].logsDir, "~/.claude/projects")
    }

    func testSetSourceToTheCurrentSourceOrAnUnknownAccountChangesNothing() {
        var s = pair()
        s.accounts[0].logsDir = nil
        let before = s
        XCTAssertFalse(s.setSource(.claudeCode, for: s.accounts[0].id))
        XCTAssertFalse(s.setSource(.safariSession, for: UUID()))
        XCTAssertEqual(s, before)
    }
}
