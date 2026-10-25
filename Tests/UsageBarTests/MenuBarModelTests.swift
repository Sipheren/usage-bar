import XCTest
@testable import UsageBarCore

final class MenuBarModelTests: XCTestCase {
    let now = Date()

    func snap(_ session: Double?, _ weekly: Double?, scoped: Double? = nil) -> UsageSnapshot {
        var limits: [UsageLimit] = []
        if let s = session { limits.append(UsageLimit(kind: .session, percent: s, resetsAt: now.addingTimeInterval(3600))) }
        if let w = weekly { limits.append(UsageLimit(kind: .weekly, percent: w, resetsAt: now.addingTimeInterval(86400))) }
        if let sc = scoped { limits.append(UsageLimit(kind: .scoped(id: "fable", name: "Fable"), percent: sc, resetsAt: nil)) }
        return UsageSnapshot(limits: limits, extra: nil, fetchedAt: now)
    }

    func inputs(_ p: UsageSnapshot?, _ w: UsageSnapshot?, wAge: TimeInterval = 0) -> [MenuBarInput] {
        [MenuBarInput(tag: "P", snapshot: p, lastSuccess: p == nil ? nil : now),
         MenuBarInput(tag: "W", snapshot: w, lastSuccess: w == nil ? nil : now.addingTimeInterval(-wAge))]
    }

    func build(_ i: [MenuBarInput], _ style: MenuBarStyle, metrics: MetricSelection = MetricSelection()) -> MenuBarModel {
        MenuBarModel.build(inputs: i, style: style, metrics: metrics, now: now)
    }

    func testWithPercentStyle() {
        let m = build(inputs(snap(20, 60), snap(45, 72)), .withPercent)
        XCTAssertEqual(m.plainText, "P 20% / 60%  W 45% / 72%")
    }

    func testOnlyWithPercentSpacesTheSeparator() {
        XCTAssertEqual(build(inputs(snap(20, 60), nil), .withPercent).separator, " / ")
        XCTAssertEqual(build(inputs(snap(20, 60), nil), .compact).separator, "·")
    }

    func testCompactStyle() {
        let m = build(inputs(snap(20, 60), snap(45, 72)), .compact)
        XCTAssertEqual(m.plainText, "P 20·60  W 45·72")
    }

    func testMiniBarsStyleCarriesValuesAndOptionalThirdBar() {
        let m = build(inputs(snap(20, 60, scoped: 12), snap(45, 72)), .miniBars,
                      metrics: MetricSelection(session: true, weekly: true, scoped: true))
        XCTAssertEqual(m.plainText, "P [20|60|12]  W [45|72]")
        XCTAssertEqual(m.accounts[0].values.count, 3)
    }

    func testIconOnlyIsAlwaysPlain() {
        XCTAssertEqual(build(inputs(snap(20, 60), snap(45, 72)), .iconOnly).plainText, "✳")
        // High percentages make no difference now: still the plain spark.
        XCTAssertEqual(build(inputs(snap(20, 60), snap(96, 99)), .iconOnly).plainText, "✳")
    }

    func testValuesCarryJustThePercent() {
        let m = build(inputs(snap(79.6, 96), nil), .compact)
        XCTAssertEqual(m.accounts[0].values, [MenuBarValue(percent: 80), MenuBarValue(percent: 96)])
    }

    func testMetricTogglesAndOrder() {
        let m = build(inputs(snap(20, 60, scoped: 5), nil), .compact,
                      metrics: MetricSelection(session: false, weekly: true, scoped: true))
        XCTAssertEqual(m.plainText, "P 60·5  W –")
    }

    func testMissingAccountShowsDash() {
        let m = build(inputs(snap(20, 60), nil), .withPercent)
        XCTAssertEqual(m.plainText, "P 20% / 60%  W –")
        XCTAssertTrue(m.accounts[1].missing)
    }

    func testStaleAfterFifteenMinutes() {
        let fresh = build(inputs(snap(20, 60), snap(45, 72), wAge: 14 * 60), .compact)
        XCTAssertFalse(fresh.accounts[1].stale)
        let stale = build(inputs(snap(20, 60), snap(45, 72), wAge: 16 * 60), .compact)
        XCTAssertTrue(stale.accounts[1].stale)
        XCTAssertFalse(stale.accounts[0].stale)
    }
}

final class SettingsMigrationTests: XCTestCase {
    func testFreshSettingsDefaultToMiniBarsWorkSafariAndNeedNoMigration() {
        var s = AppSettings()
        XCTAssertEqual(s.menuBarStyle, .miniBars)
        XCTAssertEqual(s.accounts.first { $0.tag == "W" }?.source, .safariSession)
        XCTAssertEqual(s.accounts.first { $0.tag == "P" }?.source, .claudeCode(configDir: "~/.claude"))
        XCTAssertFalse(s.applyMigrations())   // already migrated
    }

    func testOldSettingsDecodeAsUnmigrated() throws {
        // Settings saved before the new defaults: withPercent, Work on the Claude app, no migration key.
        let json = """
        {"menuBarStyle":"withPercent","accounts":[
          {"id":"\(UUID().uuidString)","name":"Personal","tag":"P","colorHex":"#D97757","showInMenuBar":true,
           "source":{"claudeCode":{"configDir":"~/.claude"}},"logsDir":"~/.claude/projects"},
          {"id":"\(UUID().uuidString)","name":"Work","tag":"W","colorHex":"#3E7CC4","showInMenuBar":true,
           "source":{"claudeDesktop":{}}}
        ]}
        """
        var s = try JSONDecoder().decode(AppSettings.self, from: Data(json.utf8))
        XCTAssertFalse(s.didMigrateMiniBars)
        XCTAssertEqual(s.menuBarStyle, .withPercent)

        XCTAssertTrue(s.applyMigrations())
        XCTAssertEqual(s.menuBarStyle, .miniBars)
        XCTAssertEqual(s.accounts.first { $0.tag == "W" }?.source, .safariSession)
        XCTAssertTrue(s.didMigrateMiniBars)
        XCTAssertFalse(s.applyMigrations())   // idempotent
    }

    func testConnectedDesktopAccountIsNotMovedToSafari() throws {
        let json = """
        {"menuBarStyle":"compact","desktopKeychainApproved":true,"accounts":[
          {"id":"\(UUID().uuidString)","name":"Work","tag":"W","colorHex":"#3E7CC4","showInMenuBar":true,
           "source":{"claudeDesktop":{}}}
        ]}
        """
        var s = try JSONDecoder().decode(AppSettings.self, from: Data(json.utf8))
        XCTAssertTrue(s.applyMigrations())
        XCTAssertEqual(s.menuBarStyle, .miniBars)          // style still migrates
        XCTAssertEqual(s.accounts.first?.source, .claudeDesktop)   // but a connected account is left alone
    }
}
