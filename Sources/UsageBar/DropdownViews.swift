import SwiftUI
import UsageBarCore

extension Color {
    init(hex: String) { self.init(nsColor: NSColor(hex: hex)) }
}

enum Palette {
    static let secondary = Color(nsColor: .secondaryLabelColor)
    static let track = Color.primary.opacity(0.14)
    static let separator = Color.primary.opacity(0.12)

    static func tick(_ scheme: ColorScheme) -> Color { scheme == .dark ? Color(white: 0.85) : Color(hex: "#3A3A3C") }
    static func overPace(_ scheme: ColorScheme) -> Color { scheme == .dark ? Color(hex: "#FDBA74") : Color(hex: "#9A4407") }
    static func issue(_ scheme: ColorScheme) -> Color { scheme == .dark ? Color(hex: "#FDBA74") : Color(hex: "#9A4407") }
}

struct DropdownRoot: View {
    @EnvironmentObject var store: AppStore

    var body: some View {
        Group {
            switch store.settings.dropdownLayout {
            case .bothAccounts: DropdownA()
            case .oneAtATime: DropdownB()
            }
        }
        .frame(width: 360)
        .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: - Shared pieces

struct Badge: View {
    let tag: String
    let hex: String
    var size: CGFloat = 20
    var fontSize: CGFloat = 11
    var radius: CGFloat = 5

    var body: some View {
        Text(tag)
            .font(.system(size: fontSize, weight: .bold))
            .foregroundStyle(.white)
            .frame(minWidth: size, minHeight: size)
            .padding(.horizontal, tag.count > 1 ? 2 : 0)
            .background(RoundedRectangle(cornerRadius: radius).fill(Color(hex: hex)))
    }
}

struct PaceBar: View {
    @Environment(\.colorScheme) private var scheme
    let percent: Double
    let tick: Double?
    let hex: String
    var height: CGFloat = 6
    var tickHeight: CGFloat = 12

    // The layout box is as tall as the pace tick (always, so rows line up whether or not there's a tick);
    // the bar is centred in it. Spacing around the bar is then real space, not overlapped by the tick.
    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: height / 2).fill(Palette.track)
                    .frame(height: height)
                RoundedRectangle(cornerRadius: height / 2)
                    .fill(Color(hex: hex))
                    .frame(width: max(0, min(1, percent / 100)) * w, height: height)
                if let t = tick {
                    RoundedRectangle(cornerRadius: 1)
                        .fill(Palette.tick(scheme))
                        .frame(width: 2, height: tickHeight)
                        .offset(x: max(0, min(w - 2, w * t - 1)))
                }
            }
            .frame(width: w, height: tickHeight)
        }
        .frame(height: tickHeight)
        .accessibilityElement()
        .accessibilityLabel("\(Int(percent.rounded())) percent used")
    }
}

struct RefreshButton: View {
    @EnvironmentObject var store: AppStore
    var body: some View {
        Button {
            store.refreshAll(.manual)
        } label: {
            Image(systemName: "arrow.clockwise")
                .font(.system(size: 13, weight: .medium))
                .frame(width: 28, height: 28)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.06)))
        }
        .buttonStyle(.plain)
        .help("Refresh now")
        .accessibilityLabel("Refresh now")
    }
}

struct IssueLine: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.colorScheme) private var scheme
    let issue: AccountIssue

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(issue.message)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(Palette.issue(scheme))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            if issue == .notConnected || issue == .keychainDenied {
                Button("Connect") { store.connectWork() }
                    .controlSize(.small)
                    .help("macOS will ask once to allow Usage Bar to read Claude Safe Storage. Choose Always Allow.")
            } else if issue.needsFullDiskAccess {
                Button("Open Privacy Settings…") { store.openFullDiskAccessSettings() }
                    .controlSize(.small)
                    .help("Give Usage Bar Full Disk Access so it can read Safari's cookie store.")
            }
        }
    }
}

struct LimitPresentation {
    let limit: UsageLimit
    let display: LimitDisplay

    init(_ limit: UsageLimit, now: Date) {
        self.limit = limit
        display = LimitDisplay(limit, now: now)
    }
}

/// The pace sentence. When there's no pace (window not started) an invisible placeholder keeps the
/// row the same height as the others.
struct PaceText: View {
    @Environment(\.colorScheme) private var scheme
    let p: LimitPresentation
    var body: some View {
        let over = p.display.isOverPace
        Text(p.display.paceLine ?? " ")
            .font(.system(size: 12, weight: over ? .semibold : .regular))
            .foregroundStyle(over ? Palette.overPace(scheme) : Palette.secondary)
            .opacity(p.display.paceLine == nil ? 0 : 1)
            .accessibilityHidden(p.display.paceLine == nil)
    }
}

struct LimitRowCompact: View {
    let p: LimitPresentation
    let hex: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(p.limit.name).font(.system(size: 13, weight: .medium))
                Spacer(minLength: 0)
                let pct = Text(p.display.percentText).font(.system(size: 12, weight: .semibold)).foregroundColor(.primary)
                let rest = Text(p.display.compactTrailing.map { " · \($0)" } ?? "").font(.system(size: 12))
                    .foregroundColor(Palette.secondary)
                Text("\(pct)\(rest)").monospacedDigit()
            }
            PaceBar(percent: p.limit.percent, tick: p.display.tickFraction, hex: hex)
            PaceText(p: p)
        }
    }
}

struct LimitRowLarge: View {
    let p: LimitPresentation
    let hex: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("\(p.limit.name) \(p.display.percentText) used").font(.system(size: 15, weight: .semibold)).monospacedDigit()
                Spacer(minLength: 0)
                if let r = p.display.largeTrailing {
                    Text(r).font(.system(size: 12)).foregroundStyle(Palette.secondary).monospacedDigit()
                }
            }
            PaceBar(percent: p.limit.percent, tick: p.display.tickFraction, hex: hex, height: 7, tickHeight: 13)
            PaceText(p: p)
        }
    }
}

struct ExtraUsageText: View {
    let snapshot: UsageSnapshot?
    var body: some View {
        if let e = snapshot?.extra, let line = Formatters.extraUsageLine(e) {
            Text(line).font(.system(size: 12)).foregroundStyle(Palette.secondary)
        }
    }
}

// MARK: - Layout A: both accounts

struct DropdownA: View {
    @EnvironmentObject var store: AppStore

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Claude").font(.system(size: 17, weight: .semibold))
                    Text(Formatters.updatedAgo(store.lastUpdated, now: store.now))
                        .font(.system(size: 12)).foregroundStyle(Palette.secondary)
                }
                Spacer()
                RefreshButton()
            }
            ForEach(store.settings.accounts) { account in
                Rectangle().fill(Palette.separator).frame(height: 1)
                AccountSectionA(account: account)
            }
        }
        .padding(EdgeInsets(top: 14, leading: 16, bottom: 10, trailing: 16))
    }
}

struct AccountSectionA: View {
    @EnvironmentObject var store: AppStore
    let account: Account

    var body: some View {
        let st = store.state(for: account)
        let pres = store.presentation(for: account)
        let greyed = pres.greysValues || store.isStale(account)
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Badge(tag: account.tag, hex: account.badgeHex)
                VStack(alignment: .leading, spacing: 1) {
                    Text(account.name).font(.system(size: 15, weight: .semibold))
                    if let note = pres.headerNote {
                        Text(note).font(.system(size: 11)).foregroundStyle(Palette.secondary)
                    }
                }
                Spacer()
                switch store.cost(for: account) {
                case .counting?:
                    Text("Counting…").font(.system(size: 12)).foregroundStyle(Palette.secondary)
                case .ready(let s)?:
                    Text("Today \(Formatters.money(store.local(s.todayUSD)))")
                        .font(.system(size: 12)).foregroundStyle(Palette.secondary).monospacedDigit()
                case nil:
                    EmptyView()
                }
            }
            if let issue = pres.inlineIssue { IssueLine(issue: issue) }
            if let snap = st.snapshot {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(snap.ordered) { limit in
                        LimitRowCompact(p: LimitPresentation(limit, now: store.now), hex: account.colorHex)
                    }
                    if snap.ordered.isEmpty {
                        Text("No limits reported").font(.system(size: 12)).foregroundStyle(Palette.secondary)
                    }
                    ExtraUsageText(snapshot: snap)
                }
                .opacity(greyed ? 0.5 : 1)
            } else if st.issue == nil {
                Text("Loading…").font(.system(size: 12)).foregroundStyle(Palette.secondary)
            }
            if case .ready(let s)? = store.cost(for: account) {
                Text("30 days \(Formatters.money(store.local(s.last30USD))) · \(Formatters.tokens(s.last30Tokens)) tokens")
                    .font(.system(size: 12)).foregroundStyle(Palette.secondary).monospacedDigit()
            }
        }
    }
}

// MARK: - Layout B: one account at a time

struct DropdownB: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.colorScheme) private var scheme

    var selected: Account? {
        store.settings.accounts.first { $0.id == store.selectedTab } ?? store.settings.accounts.first
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            tabs
            if let account = selected {
                AccountDetailB(account: account)
            } else {
                Text("No accounts. Add one in Settings.").font(.system(size: 12)).foregroundStyle(Palette.secondary)
            }
        }
        .padding(EdgeInsets(top: 12, leading: 16, bottom: 10, trailing: 16))
    }

    private var tabs: some View {
        HStack(spacing: 2) {
            ForEach(store.settings.accounts) { a in
                let on = a.id == selected?.id
                Button {
                    store.selectedTab = a.id
                } label: {
                    HStack(spacing: 7) {
                        Badge(tag: a.tag, hex: a.badgeHex, size: 17, fontSize: 10, radius: 4)
                        Text(a.name).font(.system(size: 13, weight: .semibold)).foregroundStyle(.primary)
                    }
                    .frame(maxWidth: .infinity, minHeight: 30)
                    .background(
                        RoundedRectangle(cornerRadius: 7)
                            .fill(on ? (scheme == .dark ? Color(white: 0.32) : Color.white) : Color.clear)
                            .shadow(color: .black.opacity(on ? 0.18 : 0), radius: 1.5, y: 1)
                    )
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: 9).fill(Color.primary.opacity(0.07)))
    }
}

struct AccountDetailB: View {
    @EnvironmentObject var store: AppStore
    let account: Account

    var body: some View {
        let st = store.state(for: account)
        let pres = store.presentation(for: account)
        let greyed = pres.greysValues || store.isStale(account)
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Claude · \(account.name)").font(.system(size: 17, weight: .semibold))
                    Text(Formatters.updatedAgo(st.lastSuccess, now: store.now))
                        .font(.system(size: 12)).foregroundStyle(Palette.secondary)
                    if let note = pres.headerNote {
                        Text(note).font(.system(size: 11)).foregroundStyle(Palette.secondary)
                    }
                }
                Spacer()
                RefreshButton()
            }
            Rectangle().fill(Palette.separator).frame(height: 1)
            if let issue = pres.inlineIssue { IssueLine(issue: issue) }
            if let snap = st.snapshot {
                VStack(alignment: .leading, spacing: 16) {
                    ForEach(snap.ordered) { limit in
                        LimitRowLarge(p: LimitPresentation(limit, now: store.now), hex: account.colorHex)
                    }
                    ExtraUsageText(snapshot: snap)
                }
                .opacity(greyed ? 0.5 : 1)
            } else if st.issue == nil {
                Text("Loading…").font(.system(size: 12)).foregroundStyle(Palette.secondary)
            }
            switch store.cost(for: account) {
            case .counting?:
                Rectangle().fill(Palette.separator).frame(height: 1)
                Text("Counting…").font(.system(size: 12)).foregroundStyle(Palette.secondary)
            case .ready(let s)?:
                CostBlockB(account: account, summary: s)
            case nil:
                EmptyView()
            }
        }
    }
}

struct CostBlockB: View {
    @EnvironmentObject var store: AppStore
    let account: Account
    let summary: CostSummary

    var body: some View {
        Rectangle().fill(Palette.separator).frame(height: 1)
        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 12) {
            GridRow {
                stat("Today", Formatters.money(store.local(summary.todayUSD)))
                stat("30 days", Formatters.money(store.local(summary.last30USD)))
            }
            GridRow {
                stat("Today tokens", Formatters.tokens(summary.todayTokens))
                stat("30-day tokens", Formatters.tokens(summary.last30Tokens))
            }
        }
        let daily = summary.dailyUSD.map(store.local)
        let maxDay = daily.max() ?? 0
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Cost per day, last 30 days")
                Spacer()
                Text("max A$\(Int(maxDay.rounded()))").monospacedDigit()
            }
            .font(.system(size: 12)).foregroundStyle(Palette.secondary)
            HStack(alignment: .bottom, spacing: 3) {
                ForEach(Array(daily.enumerated()), id: \.offset) { _, v in
                    UnevenRoundedRectangle(topLeadingRadius: 2, topTrailingRadius: 2)
                        .fill(Color(hex: account.colorHex).opacity(0.8))
                        .frame(maxWidth: .infinity)
                        .frame(height: max(2, maxDay > 0 ? CGFloat(v / maxDay) * 64 : 2))
                }
            }
            .frame(height: 64, alignment: .bottom)
            .overlay(alignment: .bottom) { Rectangle().fill(Color.primary.opacity(0.2)).frame(height: 1) }
        }
        VStack(alignment: .leading, spacing: 2) {
            if let top = summary.topModel { Text("Top model: \(top)").font(.system(size: 12)) }
            Text(footnote).font(.system(size: 12)).foregroundStyle(Palette.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var footnote: String {
        var s = "Cost and tokens estimated from this Mac's Claude Code logs at API rates."
        if !summary.unpricedModels.isEmpty {
            s += " Unpriced model\(summary.unpricedModels.count > 1 ? "s" : ""): "
                + summary.unpricedModels.joined(separator: ", ") + " (counted as A$0)."
        }
        return s
    }

    private func stat(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.system(size: 12)).foregroundStyle(Palette.secondary)
            Text(value).font(.system(size: 17, weight: .semibold)).monospacedDigit()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
