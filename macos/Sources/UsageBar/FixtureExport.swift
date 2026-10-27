import AppKit
import Foundation
import UsageBarCore

/// `--export-fixtures DIR` and `--fixtures-expected DIR`: anonymised API fixtures and their answer sheet,
/// for testing a port without real accounts. Raw responses stay in memory; only anonymised JSON is written,
/// and only after a leak check passes. Prints statuses and counts, never a raw value.
extension DebugCommands {
    static func exportFixtures(to dir: URL) async -> Int32 {
        let me = getpid()
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: "com.sipheren.usagebar")
            .filter { $0.processIdentifier != me }
        guard others.isEmpty else {
            print("Usage Bar is running. Quit it first: it fetches each account as soon as it is due, "
                  + "and it would overwrite the saved fetch times this command records.")
            return 1
        }

        var settings = AppSettings(accounts: Account.personalAndWork())   // no saved settings: the original pair
        if let data = UserDefaults.standard.data(forKey: "settings.v1"),
           var saved = try? JSONDecoder().decode(AppSettings.self, from: data) {
            _ = saved.applyMigrations()
            settings = saved
        }
        let stateURL = Paths.appSupport.appendingPathComponent("state.json")
        var states = (try? Data(contentsOf: stateURL)).flatMap {
            try? JSONDecoder().decode([String: PersistedAccountState].self, from: $0)
        } ?? [:]

        let api = dir.appendingPathComponent("api", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: api, withIntermediateDirectories: true)
        } catch {
            print("Can't create \(api.path): \(error.localizedDescription)")
            return 1
        }

        let cli = ClaudeCodeAccountInfo.read()
        // Fake ids are consistent within one run. Files kept from an earlier run keep their own ids, so start
        // numbering after the highest one already in the folder rather than reuse a number for another id.
        let existing = ((try? FileManager.default.contentsOfDirectory(at: api, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.pathExtension == "json" }
            .compactMap { try? String(contentsOf: $0, encoding: .utf8) }
            .map(FixtureAnonymizer.highestFakeNumber).max() ?? 0
        var anonymizer = FixtureAnonymizer(personalIDs: Set([cli?.accountUuid, cli?.organizationUuid].compactMap { $0 }),
                                           firstNumber: existing + 1)
        let fetcher = AccountFetcher()
        var requests = 0
        var written: [String] = []

        func due(_ account: Account) -> Bool {
            let st = states[account.id.uuidString]
            // The export reads the Claude Code login's token and Safari's cookie, never the Claude app.
            let interval = FetchPolicy.backgroundInterval(
                userMinutes: settings.refreshMinutes,
                tokenSource: FetchPolicy.isTokenSource(account.source.kind, lastRoute: nil))
            let ok = FetchPolicy.shouldFetch(.launch, lastAttempt: st?.lastAttempt, waitUntil: st?.waitUntil,
                                             backgroundInterval: interval, now: Date())
            if !ok {
                let next = [st?.waitUntil, st?.lastAttempt.map { $0.addingTimeInterval(interval) }].compactMap { $0 }.max()
                print("\(account.tag): not due (next allowed \(next.map { Formatters.clockTime($0) } ?? "later")); "
                      + "no raw response is kept, so skipped")
            }
            return ok
        }

        func exists(_ name: String) -> Bool { FileManager.default.fileExists(atPath: api.appendingPathComponent(name).path) }

        /// Records the attempt like the app would, so a relaunch doesn't spend another request.
        func record(_ account: Account, _ r: RawResponse, isUsage: Bool) {
            let key = account.id.uuidString
            var st = states[key] ?? PersistedAccountState(snapshot: nil, lastSuccess: nil, lastAttempt: nil,
                                                          waitUntil: nil, email: nil)
            let now = Date()
            st.lastAttempt = now
            if isUsage, r.status == 200, let snap = try? UsageDecoder.decode(r.data, fetchedAt: now) {
                st.snapshot = snap
                st.lastSuccess = now
                st.waitUntil = nil
            } else if r.status == 429 {
                st.waitUntil = now.addingTimeInterval(FetchPolicy.rateLimitWait(UsageClient.retryAfter(r.retryAfter, now: now)))
            }
            states[key] = st
        }

        /// Anonymises and writes one response if it is a 200 and passes the leak check.
        func save(_ r: RawResponse, _ document: FixtureAnonymizer.Document, _ name: String) -> Bool {
            requests += 1
            guard r.status == 200 else {
                print("  \(name): HTTP \(r.status)\(r.isCloudflareChallenge ? " (Cloudflare challenge)" : ""), not written")
                return false
            }
            guard let raw = try? JSONValue.parse(r.data) else {
                print("  \(name): HTTP 200 but not JSON, not written")
                return false
            }
            let out = anonymizer.anonymize(raw, document: document)
            let check = anonymizer.leaks(raw: raw, anonymized: out)
            guard check.clean else {
                print("  \(name): withheld, leak check found \(check.strings) string(s) and \(check.numbers) number(s)"
                      + (check.paths.isEmpty ? "" : " at " + check.paths.joined(separator: ", ")))
                return false
            }
            do {
                try out.serialized().write(to: api.appendingPathComponent(name), atomically: true, encoding: .utf8)
                written.append(name)
                print("  \(name): HTTP 200, written (leak check clean)")
                return true
            } catch {
                print("  \(name): write failed: \(error.localizedDescription)")
                return false
            }
        }

        // Files already in DIR/api are kept and not fetched again; delete one to refetch it.
        // Personal: the Claude Code login, through the OAuth usage and profile endpoints.
        let needUsage = !exists("oauth-usage.json"), needProfile = !exists("oauth-profile.json")
        if let personal = settings.accounts.first(where: { $0.source.isClaudeCode }) {
            if !needUsage && !needProfile {
                print("\(personal.tag): oauth-usage.json and oauth-profile.json already exist, not fetched")
            } else if due(personal) {
            print("\(personal.tag): Claude Code login")
            if case .claudeCode(let configDir) = personal.source,
               let token = try? await ClaudeCodeCredentials.read(configDir: configDir), !token.isExpired(now: Date()) {
                do {
                    var limited = false
                    if needUsage {
                        let usage = try await fetcher.client.rawGET(UsageClient.usageURL, token: token.accessToken)
                        record(personal, usage, isUsage: true)
                        _ = save(usage, .oauthUsage, "oauth-usage.json")
                        limited = usage.status == 429
                    }
                    if needProfile && !limited {
                        let profile = try await fetcher.client.rawGET(UsageClient.profileURL, token: token.accessToken)
                        if !needUsage { record(personal, profile, isUsage: false) }
                        _ = save(profile, .oauthProfile, "oauth-profile.json")
                    }
                } catch {
                    print("  network error, nothing written")
                }
            } else {
                print("  no usable Claude Code login, skipped")
            }
            }
        }

        // Work: the claude.ai route (organizations, then the chosen organization's usage).
        if let work = settings.accounts.first(where: { !$0.source.isClaudeCode }) {
            let needOrgs = !exists("claude-ai-organizations.json"), needOrgUsage = !exists("claude-ai-org-usage.json")
            if !needOrgs && !needOrgUsage {
                print("\(work.tag): claude-ai-organizations.json and claude-ai-org-usage.json already exist, not fetched")
            } else if work.source != .safariSession {
                print("\(work.tag): uses the Claude app source; the export reads the claude.ai route through Safari only, skipped")
            } else if due(work) {
                print("\(work.tag): Safari session")
                do {
                    let jar = try SafariCookieReader().claudeCookieJar()
                    guard jar["sessionKey"] != nil else { throw SafariCookieError.notFound }
                    // The raw list is needed to find the organization's real id, even when its fixture exists.
                    let orgsRaw = try await fetcher.cookieClient.rawGET(ClaudeAiCookieClient.orgsURL, cookies: jar)
                    let orgsOK: Bool
                    if needOrgs {
                        orgsOK = save(orgsRaw, .claudeAiOrganizations, "claude-ai-organizations.json")
                    } else {
                        requests += 1
                        orgsOK = orgsRaw.status == 200
                        print("  claude-ai-organizations.json: exists, kept (fetched for the organization id, HTTP \(orgsRaw.status))")
                    }
                    if orgsOK && needOrgUsage {
                        let orgs = ClaudeAiCookieClient.parseOrganizations(orgsRaw.data)
                        if let org = ClaudeAiCookieClient.pickOrg(orgs: orgs, lastActiveOrg: jar["lastActiveOrg"],
                                                                  personalOrg: cli?.organizationUuid),
                           !ClaudeAiCookieClient.isPersonalOrg(org, personalOrg: cli?.organizationUuid),
                           let url = URL(string: "https://claude.ai/api/organizations/\(org.uuid)/usage") {
                            let usage = try await fetcher.cookieClient.rawGET(url, cookies: jar)
                            record(work, usage, isUsage: true)
                            _ = save(usage, .claudeAiOrgUsage, "claude-ai-org-usage.json")
                        } else {
                            record(work, orgsRaw, isUsage: false)
                            print("  no non-personal organization to read, org usage skipped")
                        }
                    } else {
                        record(work, orgsRaw, isUsage: false)
                    }
                } catch SafariCookieError.fullDiskAccess {
                    print("  no Full Disk Access in this process; launch it with `open` so Usage Bar's own grant applies")
                } catch {
                    print("  no claude.ai session or network error, nothing written")
                }
            }
        }

        do {
            try FileManager.default.createDirectory(at: Paths.appSupport, withIntermediateDirectories: true)
            try JSONEncoder().encode(states).write(to: stateURL, options: .atomic)
        } catch {
            print("Could not save fetch times: \(error.localizedDescription)")
        }

        print("Requests made: \(requests). Files written: \(written.isEmpty ? "none" : written.joined(separator: ", ")).")
        print("Strings kept as contract values (\(anonymizer.report.keptValues.count)):")
        for v in anonymizer.report.keptValues.sorted() { print("  \(v)") }
        print("Paths with replaced values: \(anonymizer.report.replacedPaths.count)")
        return writeExpected(to: dir) == 0 || !written.isEmpty ? 0 : 1
    }

    /// Regenerates `expected.json` from the folder. No network.
    static func writeExpected(to dir: URL) -> Int32 {
        let target = dir.appendingPathComponent("expected.json")
        do {
            try ParitySheet.build(fixtures: dir).serialized().write(to: target, atomically: true, encoding: .utf8)
            print("Wrote \(target.path)")
            return 0
        } catch {
            print("Could not write \(target.path): \(error.localizedDescription)")
            return 1
        }
    }
}
