import Foundation

/// Display currency for cost figures. Costs are computed in US dollars and converted with the daily
/// rate; with USD chosen nothing is converted and no rate is ever fetched.
public enum Currency {
    /// Offered in Settings, in this order. The locale's currency joins them when the rate list has it.
    public static let builtIn = ["AUD", "USD", "EUR", "GBP", "NZD", "CAD", "JPY"]

    public static func options(localeCode: String?, available: Set<String>) -> [String] {
        guard let code = localeCode?.uppercased(), !builtIn.contains(code), available.contains(code) else { return builtIn }
        return builtIn + [code]
    }

    public static func needsRate(_ code: String) -> Bool { code.uppercased() != "USD" }

    /// US-dollar amount in `code`, or nil when that currency's rate isn't known yet.
    public static func convert(usd: Double, to code: String, rates: ExchangeRates) -> Double? {
        guard needsRate(code) else { return usd }
        return rates.rates[code.uppercased()].map { usd * $0 }
    }

    public static func symbol(_ code: String) -> String {
        switch code.uppercased() {
        case "AUD": return "A$"
        case "USD": return "US$"
        case "EUR": return "€"
        case "GBP": return "£"
        case "NZD": return "NZ$"
        case "CAD": return "C$"
        case "JPY": return "¥"
        default: return code.uppercased() + " "
        }
    }

    /// Currencies with no minor unit never show decimals.
    public static func hasDecimals(_ code: String) -> Bool { !["JPY", "KRW", "VND", "ISK", "CLP"].contains(code.uppercased()) }

    /// "A$6.20" below 100, "A$1,050" from 100 up; "¥938" always whole.
    public static func format(_ amount: Double, code: String) -> String {
        let digits = hasDecimals(code) && abs(amount) < 100 ? 2 : 0
        return symbol(code) + number(amount, digits: digits)
    }

    /// Whole units, for chart labels: "A$25", "¥3,725".
    public static func formatWhole(_ amount: Double, code: String) -> String {
        symbol(code) + number(amount, digits: 0)
    }

    static func number(_ v: Double, digits: Int) -> String {
        let nf = NumberFormatter()
        nf.locale = Locale(identifier: "en_US_POSIX")
        nf.numberStyle = .decimal
        nf.usesGroupingSeparator = true
        nf.groupingSeparator = ","
        nf.decimalSeparator = "."
        nf.minimumFractionDigits = digits
        nf.maximumFractionDigits = digits
        return nf.string(from: NSNumber(value: v)) ?? String(format: "%.\(digits)f", v)
    }
}

extension Formatters {
    /// "US$1 = A$1.42 · updated 26 Oct", "… · built-in rate" before the first fetch, or nil for USD
    /// (nothing to convert). "Waiting for the exchange rate" when the chosen currency has no rate yet.
    public static func rateLine(_ rates: ExchangeRates, code: String, timeZone: TimeZone = .current) -> String? {
        guard Currency.needsRate(code) else { return nil }
        guard let rate = rates.rates[code.uppercased()] else { return "Waiting for the exchange rate" }
        let digits = Currency.hasDecimals(code) ? 2 : 0
        let base = "US$1 = \(Currency.symbol(code))\(Currency.number(rate, digits: digits))"
        if rates.isFallback { return base + " · built-in rate" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = timeZone
        f.dateFormat = "d MMM"
        return base + " · updated " + f.string(from: rates.asOf)
    }
}
