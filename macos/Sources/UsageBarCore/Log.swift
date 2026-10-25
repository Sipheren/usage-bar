import os

/// Unified logging. Read with:
///   /usr/bin/log stream --predicate 'subsystem == "com.sipheren.usagebar"' --level info
///
/// Rules: never log tokens, cookies, emails, organization names or file paths under the home folder.
/// Log statuses, source kinds, HTTP codes and counts only, and mark only those as `.public`.
public enum Log {
    public static let subsystem = "com.sipheren.usagebar"
    public static let fetch = Logger(subsystem: subsystem, category: "fetch")
    public static let cost = Logger(subsystem: subsystem, category: "cost")
    public static let ui = Logger(subsystem: subsystem, category: "ui")
    public static let auth = Logger(subsystem: subsystem, category: "auth")
}
