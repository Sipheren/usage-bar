import Foundation

public enum Formatters {
    static let posix = Locale(identifier: "en_US_POSIX")

    private static func formatter(_ format: String, timeZone: TimeZone) -> DateFormatter {
        let f = DateFormatter()
        f.locale = posix
        f.timeZone = timeZone
        f.dateFormat = format
        f.amSymbol = "am"
        f.pmSymbol = "pm"
        return f
    }

    /// "in 2h 10m" under 24 h, otherwise "Thu 9:00 am" in the given time zone. Callers prefix "Resets"/"resets".
    public static func resetPhrase(_ resetsAt: Date, now: Date, timeZone: TimeZone = .current) -> String {
        let seconds = resetsAt.timeIntervalSince(now)
        if seconds <= 0 { return "now" }
        if seconds < 24 * 3600 {
            let totalMinutes = Int((seconds / 60).rounded(.up))
            let h = totalMinutes / 60, m = totalMinutes % 60
            if h == 0 { return "in \(m)m" }
            if m == 0 { return "in \(h)h" }
            return "in \(h)h \(m)m"
        }
        // The API sends resets a fraction of a second early (14:59:59.6Z for 15:00Z); round, don't truncate.
        let nearestMinute = Date(timeIntervalSince1970: (resetsAt.timeIntervalSince1970 / 60).rounded() * 60)
        return formatter("EEE h:mm a", timeZone: timeZone).string(from: nearestMinute)
    }

    /// Run-out estimate, rounded to 10 minutes: "3:40 pm" today, "Wed 7 pm" on another day.
    public static func runOutTime(_ date: Date, now: Date, timeZone: TimeZone = .current) -> String {
        let rounded = Date(timeIntervalSince1970: (date.timeIntervalSince1970 / 600).rounded() * 600)
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = timeZone
        let minute = cal.component(.minute, from: rounded)
        let time = minute == 0 ? "h a" : "h:mm a"
        let sameDay = cal.isDate(rounded, inSameDayAs: now)
        return formatter(sameDay ? time : "EEE \(time)", timeZone: timeZone).string(from: rounded)
    }

    /// "6:40 am".
    public static func clockTime(_ date: Date, timeZone: TimeZone = .current) -> String {
        formatter("h:mm a", timeZone: timeZone).string(from: date)
    }

    /// "Updated just now", "Updated 2 min ago", "Updated 3 h ago".
    public static func updatedAgo(_ date: Date?, now: Date) -> String {
        guard let date else { return "Not updated yet" }
        let minutes = Int(max(0, now.timeIntervalSince(date)) / 60)
        if minutes < 1 { return "Updated just now" }
        if minutes < 60 { return "Updated \(minutes) min ago" }
        let hours = minutes / 60
        if hours < 48 { return "Updated \(hours) h ago" }
        return "Updated \(hours / 24) days ago"
    }

    /// Cost in the display currency: "A$6.20" below 100, "A$1,050" from 100 up.
    public static func money(_ amount: Double, symbol: String = "A$") -> String {
        let nf = NumberFormatter()
        nf.locale = posix
        nf.numberStyle = .decimal
        nf.usesGroupingSeparator = true
        nf.groupingSeparator = ","
        nf.decimalSeparator = "."
        let digits = abs(amount) < 100 ? 2 : 0
        nf.minimumFractionDigits = digits
        nf.maximumFractionDigits = digits
        return symbol + (nf.string(from: NSNumber(value: amount)) ?? String(format: "%.2f", amount))
    }

    /// Whole amounts drop the decimals ("A$50"), others keep them ("A$12.50").
    public static func minorUnits(_ minor: Double, decimalPlaces: Int, currency: String?) -> String {
        let value = minor / pow(10, Double(max(0, decimalPlaces)))
        let symbol = currencySymbol(currency)
        let nf = NumberFormatter()
        nf.locale = posix
        nf.numberStyle = .decimal
        nf.groupingSeparator = ","
        nf.usesGroupingSeparator = true
        let whole = value.rounded() == value
        nf.minimumFractionDigits = whole ? 0 : max(2, decimalPlaces)
        nf.maximumFractionDigits = whole ? 0 : max(2, decimalPlaces)
        return symbol + (nf.string(from: NSNumber(value: value)) ?? "\(value)")
    }

    public static func currencySymbol(_ code: String?) -> String {
        switch (code ?? "").uppercased() {
        case "AUD": return "A$"
        case "USD": return "US$"
        case "EUR": return "€"
        case "GBP": return "£"
        case "NZD": return "NZ$"
        case "CAD": return "C$"
        case "": return "$"
        default: return code!.uppercased() + " "
        }
    }

    /// "Extra usage A$12.50 of A$50 · off (out of credits)".
    public static func extraUsageLine(_ e: ExtraUsage) -> String? {
        guard let used = e.usedCredits else { return nil }
        let dp = e.decimalPlaces ?? 2
        var s = "Extra usage " + minorUnits(used, decimalPlaces: dp, currency: e.currency)
        if let limit = e.monthlyLimit {
            s += " of " + minorUnits(limit, decimalPlaces: dp, currency: e.currency)
        }
        if e.isEnabled == false {
            if let reason = e.disabledReason, !reason.isEmpty {
                s += " · off (" + reason.replacingOccurrences(of: "_", with: " ") + ")"
            } else {
                s += " · off"
            }
        }
        return s
    }

    /// "260M", "1.4B", "9.2M", "86M", "12K".
    public static func tokens(_ n: Int64) -> String {
        let v = Double(n)
        func fmt(_ x: Double, _ unit: String) -> String {
            if x < 10 {
                let one = (x * 10).rounded() / 10
                return (one == one.rounded() ? String(Int(one)) : String(format: "%.1f", one)) + unit
            }
            return String(Int(x.rounded())) + unit
        }
        if v >= 1e9 { return fmt(v / 1e9, "B") }
        if v >= 1e6 { return fmt(v / 1e6, "M") }
        if v >= 1e3 { return fmt(v / 1e3, "K") }
        return String(n)
    }

    /// "25 Oct 2026".
    public static func shortDate(_ d: Date, timeZone: TimeZone = .current) -> String {
        formatter("d MMM yyyy", timeZone: timeZone).string(from: d)
    }
}
