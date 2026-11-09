import Foundation

let finished = DispatchSemaphore(value: 0)
let gate = NSLock()
var forced = 0
var completed = 0
let deadline = TerminationDeadline(seconds: 0.05, force: { gate.lock(); forced += 1; gate.unlock() }, finished: { gate.lock(); completed += 1; gate.unlock(); finished.signal() })
deadline.start()
deadline.start()
precondition(finished.wait(timeout: .now() + 2) == .success)
deadline.complete()
gate.lock(); precondition(forced == 1 && completed == 1); gate.unlock()
let quick = TerminationDeadline(seconds: 0.2, force: { fatalError("A completed child must not be killed") }, finished: { finished.signal() })
quick.start(); quick.complete(); quick.complete()
precondition(finished.wait(timeout: .now() + 2) == .success)
Thread.sleep(forTimeInterval: 0.25)
print("Swift deadline expiry, fast exit and repeated requests: PASS")
