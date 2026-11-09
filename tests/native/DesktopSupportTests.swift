import Foundation
import AppKit

@main struct DesktopSupportChecks {
    static func main() throws {
        var count = 0
        func check(_ condition: @autoclosure () -> Bool) { precondition(condition()); count += 1 }
        func rejects(_ action: () throws -> Void) { do { try action(); fatalError("Expected invalid configuration") } catch { count += 1 } }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("agm-desktop-native-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("installation.json")
        let absent = try DesktopInstallation.read(file); check(absent == nil)
        try FileManager.default.createDirectory(at: file, withIntermediateDirectories: false)
        rejects { _ = try DesktopInstallation.read(file) }; try FileManager.default.removeItem(at: file)
        try FileManager.default.createSymbolicLink(atPath: file.path, withDestinationPath: file.path + ".missing")
        rejects { _ = try DesktopInstallation.read(file) }; try FileManager.default.removeItem(at: file)
        var row: [String: Any] = ["schemaVersion": "ag-desktop/install-v1", "appId": "agmemory", "dataDirectory": root.appendingPathComponent("data").path,
                                 "backupDirectory": root.appendingPathComponent("backup").path, "applicationPath": root.appendingPathComponent("AgMemory.app").path]
        func write() throws { try JSONSerialization.data(withJSONObject: row).write(to: file) }
        try write(); let valid = try DesktopInstallation.read(file)!
        check(valid.closeToTray == nil)
        check(DesktopInstallation.locations(valid, environment: [:]).backup.path == row["backupDirectory"] as? String)
        let blank = DesktopInstallation.locations(valid, environment: ["AGMEMORY_DATA_DIR": " ", "AGMEMORY_BACKUP_DIR": ""])
        let fallback = DesktopInstallation.locations(valid, environment: [:])
        check(blank.data == fallback.data && blank.backup == fallback.backup)
        let override = root.appendingPathComponent("override").path
        check(DesktopInstallation.locations(valid, environment: ["AGMEMORY_DATA_DIR": override]).data.path == override)
        check(DesktopInstallation.locations(valid, environment: ["AGMEMORY_DATA_DIR": override]).backup.path != row["backupDirectory"] as? String)
        row["preferredPort"] = 80; try write(); rejects { _ = try DesktopInstallation.read(file) }
        row["preferredPort"] = 8891; row["backupDirectory"] = row["dataDirectory"]; try write(); rejects { _ = try DesktopInstallation.read(file) }
        row["backupDirectory"] = root.appendingPathComponent("backup").path; row["dataDirectory"] = "/"; try write(); rejects { _ = try DesktopInstallation.read(file) }
        try Data(repeating: 32, count: 65537).write(to: file); rejects { _ = try DesktopInstallation.read(file) }
        check(DesktopBackground.shouldHide(closeToTray: true, ready: true, quitting: false))
        check(!DesktopBackground.shouldHide(closeToTray: true, ready: false, quitting: false))
        check(!DesktopBackground.shouldHide(closeToTray: false, ready: true, quitting: false))
        check(!DesktopBackground.shouldHide(closeToTray: true, ready: true, quitting: true))
        check(DesktopRelease.version("v1.5.0") == [1,5,0])
        for invalid in ["1.05.0", "1.5", "-1.5.0", "1.5.0-beta", "1.5.0/../evil", "١.٥.٠"] { check(DesktopRelease.version(invalid) == nil) }
        let asset = "AgMemory-1.6.0-osx-arm64.dmg"
        let url = URL(string: "https://github.com/PonomarevRA/ag-memmory/releases/download/v1.6.0/" + asset)!
        check(DesktopRelease.validInstaller(url, tag: "v1.6.0", name: asset, architecture: "arm64"))
        check(!DesktopRelease.validInstaller(url, tag: "v1.6.0", name: asset, architecture: "x64"))
        check(!DesktopRelease.validInstaller(url, tag: "v1.6.0", name: asset, architecture: "other"))
        check(!DesktopRelease.validInstaller(URL(string: url.absoluteString + "?x=1")!, tag: "v1.6.0", name: asset, architecture: "arm64"))
        check(!DesktopRelease.validInstaller(URL(string: url.absoluteString.replacingOccurrences(of: "github.com", with: "attacker.invalid"))!, tag: "v1.6.0", name: asset, architecture: "arm64"))
        let release: [String: Any] = ["tag_name": "v1.6.0", "draft": false, "prerelease": false, "assets": [["name": asset, "browser_download_url": url.absoluteString]]]
        let bytes = try JSONSerialization.data(withJSONObject: release)
        let found = try DesktopRelease.parse(bytes, current: "1.5.0", architecture: "arm64")
        check(found?.version == "1.6.0" && found?.installer == url)
        let same = try DesktopRelease.parse(bytes, current: "1.6.0", architecture: "arm64"); check(same == nil)
        rejects { _ = try DesktopRelease.parse(Data(repeating: 32, count: 262145), current: "1.5.0", architecture: "arm64") }
        print("\(count) desktop distribution checks passed")
    }
}
