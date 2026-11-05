import Foundation

public struct ReviewRecord: Codable, Equatable, Sendable {
    public var lastShown: Date?
    public var day: Date?
    public var remembered = 0
    public var skipped = 0
    public init() {}
    public func counts(on now: Date, calendar: Calendar) -> (Int, Int) {
        guard let day, calendar.isDate(day, inSameDayAs: now) else { return (0, 0) }
        return (remembered, skipped)
    }
    public func weight(added: Date, now: Date, calendar: Calendar = .current) -> Double {
        let days = { (date: Date) in max(0, now.timeIntervalSince(date) / 86_400) }
        let (m, s) = counts(on: now, calendar: calendar)
        let ratio = (Double(m) + 1) / (Double(m + s) + 2)
        return 0.5 + 3 * log1p(days(lastShown ?? added)) + exp(-days(added) / 3)
            + 2 * (1 - ratio) + Double(min(s, 3)) / 3
    }
}

public struct ReviewHistory: Codable, Sendable {
    public private(set) var records: [String: ReviewRecord] = [:]
    public private(set) var currentExposure: UUID?
    public private(set) var currentNoteID: String?
    public init() {}

    public func draw(_ notes: [LibraryNote], now: Date = Date(), unit: Double = .random(in: 0..<1)) -> LibraryNote? {
        let choices = notes.filter(\.isWord).map {
            ($0, (records[$0.id] ?? ReviewRecord()).weight(added: $0.added, now: now))
        }
        var needle = min(max(0, unit), 1) * choices.reduce(0) { $0 + $1.1 }
        for (note, weight) in choices {
            needle -= weight
            if needle < 0 { return note }
        }
        return choices.last?.0
    }
    @discardableResult
    public mutating func show(_ id: String, now: Date = Date()) -> UUID {
        let exposure = UUID()
        currentExposure = exposure
        currentNoteID = id
        records[id, default: ReviewRecord()].lastShown = now
        return exposure
    }
    public mutating func finish(_ exposure: UUID, remembered: Bool, now: Date = Date(), calendar: Calendar = .current) {
        guard currentExposure == exposure, let id = currentNoteID else { return }
        var record = records[id] ?? ReviewRecord()
        let (m, s) = record.counts(on: now, calendar: calendar)
        record.day = calendar.startOfDay(for: now)
        record.remembered = m + (remembered ? 1 : 0)
        record.skipped = s + (remembered ? 0 : 1)
        records[id] = record
        currentExposure = nil
        currentNoteID = nil
    }
}
