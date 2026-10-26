import XCTest
@testable import UsageBarCore

final class CurrencyTests: XCTestCase {
    let utc = TimeZone(identifier: "UTC")!
    lazy var asOf = ISO8601DateFormatter().date(from: "2026-10-26T00:10:00Z")!
    lazy var fetched = ExchangeRates(rates: ["USD": 1, "AUD": 1.4231, "EUR": 0.92, "JPY": 151.3, "CHF": 0.88],
                                     asOf: asOf, fetchedAt: asOf)

    func testUSDNeedsNoRateAndNoConversion() {
        XCTAssertFalse(Currency.needsRate("USD"))
        XCTAssertEqual(Currency.convert(usd: 12.5, to: "USD", rates: .fallback), 12.5)
        XCTAssertEqual(Currency.convert(usd: 12.5, to: "USD", rates: ExchangeRates(rates: [:], asOf: asOf, fetchedAt: asOf)), 12.5)
    }

    func testConvertsWithTheChosenRate() {
        XCTAssertTrue(Currency.needsRate("AUD"))
        XCTAssertEqual(Currency.convert(usd: 10, to: "AUD", rates: fetched)!, 14.231, accuracy: 1e-9)
        XCTAssertEqual(Currency.convert(usd: 10, to: "EUR", rates: fetched)!, 9.2, accuracy: 1e-9)
        XCTAssertNil(Currency.convert(usd: 10, to: "GBP", rates: fetched))   // no rate yet
    }

    func testBuiltInRateKeepsAUDWorkingBeforeTheFirstFetch() {
        XCTAssertEqual(Currency.convert(usd: 10, to: "AUD", rates: .fallback)!, 14.23, accuracy: 1e-9)
        XCTAssertTrue(ExchangeRates.fallback.isFallback)
    }

    func testFormattingUsesSymbolAndDecimals() {
        XCTAssertEqual(Currency.format(6.2, code: "AUD"), "A$6.20")
        XCTAssertEqual(Currency.format(1050.2, code: "AUD"), "A$1,050")
        XCTAssertEqual(Currency.format(6.2, code: "USD"), "US$6.20")
        XCTAssertEqual(Currency.format(214.4, code: "EUR"), "€214")
        XCTAssertEqual(Currency.format(9.5, code: "GBP"), "£9.50")
        XCTAssertEqual(Currency.format(9.5, code: "NZD"), "NZ$9.50")
        XCTAssertEqual(Currency.format(9.5, code: "CAD"), "C$9.50")
        // Yen has no minor unit: never decimals, even for small amounts.
        XCTAssertEqual(Currency.format(938.06, code: "JPY"), "¥938")
        XCTAssertEqual(Currency.format(32_380.4, code: "JPY"), "¥32,380")
        XCTAssertEqual(Currency.format(9.5, code: "CHF"), "CHF 9.50")
    }

    func testWholeAmountFormatForChartLabels() {
        XCTAssertEqual(Currency.formatWhole(24.6, code: "AUD"), "A$25")
        XCTAssertEqual(Currency.formatWhole(3_725.1, code: "JPY"), "¥3,725")
    }

    func testOptionsAddTheLocaleCurrencyOnlyWhenTheRateListHasIt() {
        let base = ["AUD", "USD", "EUR", "GBP", "NZD", "CAD", "JPY"]
        XCTAssertEqual(Currency.options(localeCode: "AUD", available: Set(fetched.rates.keys)), base)
        XCTAssertEqual(Currency.options(localeCode: "CHF", available: Set(fetched.rates.keys)), base + ["CHF"])
        XCTAssertEqual(Currency.options(localeCode: "SEK", available: Set(fetched.rates.keys)), base)
        XCTAssertEqual(Currency.options(localeCode: nil, available: []), base)
    }

    func testRateLineOnlyWhenConverting() {
        XCTAssertNil(Formatters.rateLine(fetched, code: "USD", timeZone: utc))
        XCTAssertEqual(Formatters.rateLine(fetched, code: "AUD", timeZone: utc), "US$1 = A$1.42 · updated 26 Oct")
        XCTAssertEqual(Formatters.rateLine(fetched, code: "JPY", timeZone: utc), "US$1 = ¥151 · updated 26 Oct")
        XCTAssertEqual(Formatters.rateLine(.fallback, code: "AUD", timeZone: utc), "US$1 = A$1.42 · built-in rate")
        XCTAssertEqual(Formatters.rateLine(.fallback, code: "EUR", timeZone: utc), "Waiting for the exchange rate")
    }

    func testOldSettingsDefaultToAUD() throws {
        let s = try JSONDecoder().decode(AppSettings.self, from: Data(#"{"menuBarStyle":"compact"}"#.utf8))
        XCTAssertEqual(s.currency, "AUD")
        XCTAssertEqual(AppSettings(firstRunWith: FoundSignIns()).currency, "AUD")
    }
}
