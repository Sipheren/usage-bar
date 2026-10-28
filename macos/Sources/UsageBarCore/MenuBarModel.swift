import Foundation

public struct MenuBarValue: Equatable, Sendable {
    public var percent: Int
}

public struct MenuBarAccountItem: Equatable, Sendable {
    public var tag: String
    /// Session, weekly, scoped (whichever are enabled and present), in that order.
    public var values: [MenuBarValue]
    /// No data (not signed in, never fetched): draw the tag followed by "–".
    public var missing: Bool
    /// Data older than its account's schedule allows (MenuBarModel.isStale): draw at 50% opacity.
    public var stale: Bool
}

/// What one account contributes to the menu bar.
public struct MenuBarInput: Sendable {
    public var tag: String
    public var snapshot: UsageSnapshot?
    public var lastSuccess: Date?
    /// The account's current background interval (with backoff) and rate-limit wait, for staleness.
    public var backgroundInterval: TimeInterval
    public var waitUntil: Date?

    public init(tag: String, snapshot: UsageSnapshot?, lastSuccess: Date?, backgroundInterval: TimeInterval = 0,
                waitUntil: Date? = nil) {
        self.tag = tag
        self.snapshot = snapshot
        self.lastSuccess = lastSuccess
        self.backgroundInterval = backgroundInterval
        self.waitUntil = waitUntil
    }
}

/// The menu bar label. Always monochrome: the percentages never change its colour (see docs/DECISIONS.md).
public struct MenuBarModel: Equatable, Sendable {
    public var style: MenuBarStyle
    public var accounts: [MenuBarAccountItem]

    /// Nothing greys sooner than this, however short the account's interval.
    public static let staleAfter: TimeInterval = 15 * 60

    /// Stale means the account has missed checks, not just that it checks rarely (decision 19). The data
    /// may be as old as the longest of: 15 minutes; two of the account's current background intervals
    /// (backoff included); or, after a 429, the time from the last good read to the end of the wait plus
    /// 15 minutes.
    public static func isStale(lastSuccess: Date?, backgroundInterval: TimeInterval, waitUntil: Date?, now: Date) -> Bool {
        guard let last = lastSuccess else { return false }
        var allowed = max(staleAfter, 2 * backgroundInterval)
        if let wait = waitUntil { allowed = max(allowed, wait.timeIntervalSince(last) + staleAfter) }
        return now.timeIntervalSince(last) > allowed
    }

    public static func build(inputs: [MenuBarInput], style: MenuBarStyle, metrics: MetricSelection,
                             now: Date) -> MenuBarModel {
        var items: [MenuBarAccountItem] = []
        for input in inputs {
            var values: [MenuBarValue] = []
            if let snap = input.snapshot {
                var picked: [UsageLimit] = []
                if metrics.session, let s = snap.session { picked.append(s) }
                if metrics.weekly, let w = snap.weekly { picked.append(w) }
                // Several scoped limits collapse to the highest one: the bar has room for one.
                if metrics.scoped, let sc = snap.scoped.max(by: { $0.percent < $1.percent }) { picked.append(sc) }
                values = picked.map { MenuBarValue(percent: Int(max(0, $0.percent).rounded())) }
            }
            let stale = isStale(lastSuccess: input.lastSuccess, backgroundInterval: input.backgroundInterval,
                                waitUntil: input.waitUntil, now: now)
            items.append(MenuBarAccountItem(tag: input.tag, values: values, missing: values.isEmpty,
                                            stale: input.snapshot != nil && stale))
        }
        return MenuBarModel(style: style, accounts: items)
    }

    /// The spark alone: the Icon only style, or no account shown (none set up, or all hidden).
    public var showsSparkOnly: Bool { style == .iconOnly || accounts.isEmpty }

    /// withPercent spaces the slash ("20% / 60%"); compact uses a tight middle dot ("20·60").
    public var separator: String {
        switch style {
        case .withPercent: return " / "
        case .compact: return "·"
        case .miniBars, .iconOnly: return "/"
        }
    }

    public func valueText(_ v: MenuBarValue) -> String {
        style == .withPercent ? "\(v.percent)%" : "\(v.percent)"
    }

    /// Plain-text rendering, used for tests and accessibility: "P 20% / 60%  W 45% / 72%".
    public var plainText: String {
        if showsSparkOnly { return "✳" }
        switch style {
        case .iconOnly:
            return "✳"
        case .withPercent, .compact:
            return accounts.map { a in
                a.missing ? "\(a.tag) –" : "\(a.tag) " + a.values.map(valueText).joined(separator: separator)
            }.joined(separator: "  ")
        case .miniBars:
            return accounts.map { a in
                a.missing ? "\(a.tag) –" : "\(a.tag) [" + a.values.map { "\($0.percent)" }.joined(separator: "|") + "]"
            }.joined(separator: "  ")
        }
    }

    public var accessibilityLabel: String {
        showsSparkOnly ? "Claude usage" : "Claude usage " + plainText
    }
}
