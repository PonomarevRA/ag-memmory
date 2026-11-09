import Foundation

/// One non-resetting shutdown deadline, independent of AppKit's nested main run loop.
final class TerminationDeadline {
    private let lock = NSLock()
    private let seconds: TimeInterval
    private let force: () -> Void
    private let finished: () -> Void
    private var timer: DispatchSourceTimer?
    private var done = false

    init(seconds: TimeInterval, force: @escaping () -> Void, finished: @escaping () -> Void) {
        self.seconds = seconds
        self.force = force
        self.finished = finished
    }

    /// Arms once before sending SIGTERM, so a fast child exit cannot race timer publication.
    func start() {
        lock.lock()
        defer { lock.unlock() }
        guard timer == nil, !done else { return }
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .userInitiated))
        timer.schedule(deadline: .now() + seconds)
        timer.setEventHandler { [weak self] in self?.finish(expired: true) }
        self.timer = timer
        timer.resume()
    }

    /// Child termination and timer expiry race through exactly one completion callback.
    func complete() { finish(expired: false) }

    /// Publishes one decision under the lock, then invokes callbacks without holding it.
    private func finish(expired: Bool) {
        lock.lock()
        guard !done else { lock.unlock(); return }
        done = true
        timer?.cancel()
        lock.unlock()
        if expired { force() }
        finished()
    }
}
